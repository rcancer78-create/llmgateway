using System.Net;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteBufferedDeadlineDeltaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredHttpTimeoutStillOwnsAFormerlyBufferedSuccessOrErrorBody(bool success)
    {
        using var body = new HeldBody();
        using var http = new HttpClient(new Handler(body, success))
        {
            BaseAddress = new Uri("https://owned-gateway.invalid/"), Timeout = TimeSpan.FromSeconds(1)
        };
        using var gateway = new OpenAiGatewayClient(http);
        using var caller = new CancellationTokenSource();
        var call = gateway.GetAccountsAsync(caller.Token);
        try
        {
            await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.False(caller.IsCancellationRequested, "The configured operation timeout must fire without caller cancellation.");
            Assert.True(body.Disposed, "Timeout must join/dispose the owned body before returning.");
        }
        finally
        {
            caller.Cancel();
            body.Release.TrySetResult();
            try { await call.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private sealed class Handler(Stream body, bool success) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
            { RequestMessage = request, Content = new StreamContent(body) });
    }

    private sealed class HeldBody : Stream
    {
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
            return 0;
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
