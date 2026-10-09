using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using LLMGateway.ChatApp;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.ChatApp.Tests;

public sealed class ChatAppHttpCancellationReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task HttpBodyDeadlineIsDistinctFromCallerCancellation(bool stream, bool userCanceled) => RunStaAsync(async () =>
    {
        using var body = new HeldBody();
        using var http = new HttpClient(new Handler(body))
        {
            BaseAddress = new Uri("https://owned-gateway.invalid/"),
            Timeout = userCanceled ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(150)
        };
        using var gateway = new OpenAiGatewayClient(http);
        using var caller = new CancellationTokenSource();
        var vm = new MainViewModel(() => throw new InvalidOperationException("No native client permitted"),
            (_, _, _, _) => throw new InvalidOperationException("No server permitted"));
        var assistant = new ChatMessageViewModel(ChatRole.Assistant, "owned partial content");
        vm.Stream = stream;
        try
        {
            var operation = (Task<ChatResult?>)typeof(MainViewModel)
                .GetMethod("RunTurnAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [gateway, new ChatRequest { Model = "owned-model" }, assistant, caller.Token])!;
            await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (userCanceled) caller.Cancel();
            Assert.Null(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(userCanceled, caller.IsCancellationRequested);
            Assert.True(body.Disposed);
            if (userCanceled)
            {
                Assert.False(assistant.IsError);
                Assert.True(assistant.IncludeInHistory);
                Assert.Equal("Остановлено пользователем", assistant.Footer);
                Assert.Equal("Остановлено", vm.Status);
            }
            else
            {
                Assert.True(assistant.IsError);
                Assert.False(assistant.IncludeInHistory);
                Assert.Equal("Время ожидания истекло.", assistant.Content);
                Assert.Equal("Ошибка Timeout", assistant.Footer);
                Assert.Equal("Ошибка", vm.Status);
            }
        }
        finally { caller.Cancel(); body.Release.TrySetResult(); await vm.DisposeAsync(); }
    });

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Owned HTTP timeout GUI regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class Handler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
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
