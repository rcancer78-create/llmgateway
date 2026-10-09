using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using LLMGateway.ChatApp;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.ChatApp.Tests;

public sealed class ChatAppRetiredConnectionErrorReviewTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GatewayErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, GatewayErrorKind.RateLimited)]
    public Task RetiredConnectionPreservesItsAdmittedChatError(HttpStatusCode status, GatewayErrorKind kind) => RunStaAsync(async () =>
    {
        using var handler = new DeferredErrorHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var vm = new MainViewModel(() => throw new InvalidOperationException("No native client permitted"),
            (_, _, _, _) => throw new InvalidOperationException("No server permitted"));
        typeof(MainViewModel).GetField("_gateway", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, gateway);
        var assistant = new ChatMessageViewModel(ChatRole.Assistant, "owned pending answer");
        vm.Stream = false;
        try
        {
            var operation = (Task<ChatResult?>)typeof(MainViewModel)
                .GetMethod("RunTurnAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [gateway, new ChatRequest { Model = "owned-model" }, assistant, CancellationToken.None])!;
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.IsApiMode = true;
            handler.Release.TrySetResult();
            Assert.Null(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(assistant.IsError);
            Assert.False(assistant.IncludeInHistory);
            Assert.StartsWith($"Ошибка {kind}", assistant.Footer);
            Assert.Equal("Ошибка", vm.Status);
            Assert.Equal(1, handler.Requests);
            Assert.Contains("Режим изменён", vm.ConnectionState);
        }
        finally { handler.Release.TrySetResult(); await vm.DisposeAsync(); }
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

    private sealed class DeferredErrorHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ++Requests;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent("{\"error\":{\"message\":\"owned rejection\"}}") };
        }
    }
}
