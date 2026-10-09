using System.Net;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteStreamErrorBoundaryDeltaTests
{
    [Fact]
    public async Task NonSuccessStreamHandshakeRetainsConfiguredBodyDeadlineAndDisposesItsOwnedRead()
    {
        using var body = new HeldBody(string.Empty);
        using var http = Http(body, HttpStatusCode.InternalServerError);
        using var gateway = new OpenAiGatewayClient(http);
        using var caller = new CancellationTokenSource();
        var operation = ConsumeAsync(gateway, caller.Token);
        try
        {
            await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.False(caller.IsCancellationRequested);
            Assert.True(body.Disposed, "The failed handshake must join and dispose the actual body read.");
        }
        finally
        {
            caller.Cancel();
            body.Release.TrySetResult();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SuccessfulSseKeepsItsCallerOwnedLifetimeBeyondTheHttpHandshakeTimeout()
    {
        const string events = "data: {\"id\":\"owned\",\"model\":\"owned-model\",\"choices\":[{\"delta\":{\"content\":\"owned reply\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var body = new HeldBody(events);
        using var http = Http(body, HttpStatusCode.OK);
        using var gateway = new OpenAiGatewayClient(http);
        using var caller = new CancellationTokenSource();
        var operation = ConsumeAsync(gateway, caller.Token);
        try
        {
            await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(1400)); // Configured HTTP handshake timeout is one second.
            Assert.False(operation.IsCompleted, "A successful SSE body is owned by caller cancellation, not the finite handshake deadline.");
            Assert.False(caller.IsCancellationRequested);
            body.Release.TrySetResult();
            var updates = await operation.WaitAsync(TimeSpan.FromSeconds(5));
            var completed = Assert.Single(updates, update => update.Kind == ChatUpdateKind.Completed);
            Assert.Equal("owned reply", completed.Result!.Content);
            Assert.True(body.Disposed);
        }
        finally
        {
            caller.Cancel();
            body.Release.TrySetResult();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("{\"error\":null}", GatewayErrorKind.Upstream, null)]
    [InlineData("{\"error\":\"owned-malformed-error\"}", GatewayErrorKind.Upstream, null)]
    [InlineData("42", GatewayErrorKind.Upstream, null)]
    [InlineData("{\"error\":{}}", GatewayErrorKind.Upstream, null)]
    [InlineData("{\"error\":{\"message\":\"owned typed limit\",\"code\":\"rate_limit_exceeded\",\"retry_at\":\"2030-01-02T03:04:05+00:00\"}}", GatewayErrorKind.RateLimited, "owned typed limit")]
    public async Task SseErrorBoundaryReturnsTypedFailureAndPreservesLegitimateErrorEvidence(string data, GatewayErrorKind kind, string? message)
    {
        using var body = new ObservedBody(Encoding.UTF8.GetBytes("data: " + data + "\n\n"));
        using var http = Http(body, HttpStatusCode.OK);
        using var gateway = new OpenAiGatewayClient(http);
        var failure = await Record.ExceptionAsync(() => ConsumeAsync(gateway, CancellationToken.None));

        var error = Assert.IsType<GatewayException>(failure);
        Assert.Equal(kind, error.Kind);
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
            Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05+00:00"), error.RetryAt);
        }
        else
        {
            Assert.Null(error.RetryAt);
            Assert.DoesNotContain("owned-malformed-error", error.Message);
        }
        Assert.True(body.Disposed);
    }

    private static HttpClient Http(Stream body, HttpStatusCode status) => new(new Handler(body, status))
    {
        BaseAddress = new Uri("https://owned-gateway.invalid/"), Timeout = TimeSpan.FromSeconds(1)
    };

    private static async Task<List<ChatUpdate>> ConsumeAsync(OpenAiGatewayClient gateway, CancellationToken token)
    {
        var updates = new List<ChatUpdate>();
        await foreach (var update in gateway.StreamAsync(new() { Model = "owned-model", Messages = [ChatMessage.User("owned fixture")] }, token))
            updates.Add(update);
        return updates;
    }

    private sealed class Handler(Stream body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new StreamContent(body) });
        }
    }

    private sealed class ObservedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class HeldBody(string content) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        private int _offset;
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Reading.TrySetResult();
            await Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, _bytes.Length - _offset);
            _bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
