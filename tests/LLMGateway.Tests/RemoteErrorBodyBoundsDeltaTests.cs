using System.Net;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteErrorBodyBoundsDeltaTests
{
    private const int MaximumBodyBytes = 4 * 1024 * 1024;

    [Theory]
    [InlineData("get")]
    [InlineData("post")]
    [InlineData("delete")]
    [InlineData("login")]
    [InlineData("stream")]
    public async Task OversizedErrorStopsReadingBeforeRetainingTheEntireBody(string operation)
    {
        using var body = new GeneratedBody(MaximumBodyBytes + 64 * 1024);
        using var http = Client(body, HttpStatusCode.InternalServerError);
        using var gateway = new OpenAiGatewayClient(http);
        var failure = await Record.ExceptionAsync(() => InvokeAsync(gateway, operation, CancellationToken.None));
        Assert.Equal(GatewayErrorKind.Upstream, Assert.IsType<GatewayException>(failure).Kind);
        Assert.InRange(body.BytesRead, 1, MaximumBodyBytes + 1);
        Assert.True(body.Disposed, "The error response stream must be physically disposed before the call finishes.");
    }

    [Fact]
    public async Task ExactBudgetTypedErrorPreservesItsClassificationMessageAndRetryTime()
    {
        const string prefix = "{\"error\":{\"message\":\"owned typed error\",\"code\":\"rate_limit_exceeded\",\"retry_at\":\"2030-01-02T03:04:05+00:00\"}}";
        using var body = new GeneratedBody(MaximumBodyBytes, prefix);
        using var http = Client(body, HttpStatusCode.TooManyRequests);
        using var gateway = new OpenAiGatewayClient(http);
        var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetAccountsAsync());
        Assert.Equal(GatewayErrorKind.RateLimited, failure.Kind);
        Assert.Equal("owned typed error", failure.Message);
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05+00:00"), failure.RetryAt);
        Assert.Equal(MaximumBodyBytes, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task CallerCancellationDuringErrorBodyReadJoinsAndDisposesTheOwnedStream()
    {
        using var body = new GeneratedBody(0, holdAtEnd: true);
        using var http = Client(body, HttpStatusCode.InternalServerError);
        using var gateway = new OpenAiGatewayClient(http);
        using var cancellation = new CancellationTokenSource();
        var call = InvokeAsync(gateway, "stream", cancellation.Token);
        try
        {
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var failure = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.True(body.Disposed);
        }
        finally
        {
            cancellation.Cancel();
            body.Release.TrySetResult();
            try { await call; } catch (OperationCanceledException) { }
        }
    }

    private static HttpClient Client(Stream body, HttpStatusCode status) => new(new Handler(body, status))
    {
        BaseAddress = new Uri("https://owned-gateway.invalid/"), Timeout = TimeSpan.FromSeconds(15)
    };

    private static async Task InvokeAsync(OpenAiGatewayClient gateway, string operation, CancellationToken token)
    {
        switch (operation)
        {
            case "get": await gateway.GetAccountsAsync(token); break;
            case "post": await gateway.CheckAccountAsync("owned", token); break;
            case "delete": await gateway.RemoveAccountAsync("owned", token); break;
            case "login": await gateway.StartNativeLoginAsync("owned", token); break;
            case "stream":
                await foreach (var _ in gateway.StreamAsync(new() { Model = "owned", Messages = [ChatMessage.User("owned fixture")] }, token)) { }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class Handler(Stream body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(body), RequestMessage = request });
    }

    // Generates bounded chunks rather than allocating a giant error string in the fixture.
    private sealed class GeneratedBody(int length, string prefix = "", bool holdAtEnd = false) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (BytesRead == length && holdAtEnd)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return Fill(buffer.Span);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        private int Fill(Span<byte> target)
        {
            var count = Math.Min(Math.Min(target.Length, 4096), length - BytesRead);
            for (var index = 0; index < count; index++)
            {
                var position = BytesRead + index;
                target[index] = position < _prefix.Length ? _prefix[position] : (byte)' ';
            }
            BytesRead += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
