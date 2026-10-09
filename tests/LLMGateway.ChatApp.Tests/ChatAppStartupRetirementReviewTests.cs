using System.Reflection;
using System.Net.Http;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMGateway.ChatApp;
using LLMGateway.Server;

namespace LLMGateway.ChatApp.Tests;

/// <summary>Owned WPF dispatcher and actual loopback host; no native process or personal configuration.</summary>
public sealed class ChatAppStartupRetirementReviewTests
{
    [Fact]
    public Task DisposalWaitsForDeferredStartupAndRetiresItsActualLateServer() => RunStaAsync(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EmbeddedGatewayServer? server = null;
        string? serverKey = null;
        using var gateway = OwnedGateway();
        var viewModel = new MainViewModel(() => gateway, async (service, url, options, token) =>
        {
            serverKey = options.ApiKey;
            entered.TrySetResult();
            // A late completion may ignore cancellation; retirement must observe the actual operation.
            await release.Task;
            return server = await EmbeddedGatewayServer.StartAsync(service, url, options);
        }) { EmbeddedUrl = "http://127.0.0.1:0" };
        var startup = InvokeStartup(viewModel);
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = viewModel.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted, "Disposal must retain the unresolved startup until its physical host is retired.");
            release.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(server);
            Assert.Equal(string.Empty, viewModel.ApiKey);
            Assert.DoesNotContain(server.Url, viewModel.ServerState, StringComparison.Ordinal);
            Assert.DoesNotContain(viewModel.Log, line => line.Contains("Встроенный сервер запущен", StringComparison.Ordinal));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", serverKey);
            var failure = await Record.ExceptionAsync(() => client.GetAsync(server.Url + "/v1/gateway/accounts"));
            Assert.IsType<HttpRequestException>(failure);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => startup.WaitAsync(TimeSpan.FromSeconds(10)));
            if (stopping is not null) await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10)));
            await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask());
            if (server is not null) await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public Task StartCommandRefusesAdmissionAfterDisposal() => RunStaAsync(async () =>
    {
        using var gateway = OwnedGateway();
        var starts = 0;
        var viewModel = new MainViewModel(() => gateway, (_, _, _, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("A retired view model must never call the server factory.");
        });
        await viewModel.DisposeAsync();
        Assert.False(viewModel.StartServerCommand.CanExecute(null));
        var failure = await Record.ExceptionAsync(() => InvokeStartup(viewModel));
        Assert.Equal(0, starts);
        Assert.IsType<ObjectDisposedException>(failure);
    });

    [Fact]
    public Task SuccessfulStartupPublishesAnAuthenticatedRealServerUntilDisposal() => RunStaAsync(async () =>
    {
        using var gateway = OwnedGateway();
        var viewModel = new MainViewModel(() => gateway,
            (service, url, options, token) => EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token))
        { EmbeddedUrl = "http://127.0.0.1:0" };
        try
        {
            await InvokeStartup(viewModel).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(viewModel.ApiKey);
            Assert.Contains(viewModel.ApiUrl, viewModel.ServerState, StringComparison.Ordinal);
            using var client = new HttpClient { BaseAddress = new(viewModel.ApiUrl), Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", viewModel.ApiKey);
            using var response = await client.GetAsync("/v1/gateway/accounts");
            Assert.True(response.IsSuccessStatusCode);
        }
        finally { await viewModel.DisposeAsync(); }
    });

    private static LlmGateway OwnedGateway() => new(JsonAccountStore.InMemory([]), [], new GatewayOptions());

    private static Task InvokeStartup(MainViewModel viewModel) =>
        (Task)typeof(MainViewModel).GetMethod("StartServerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null)!;

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
        }) { IsBackground = true, Name = "Owned ChatApp retirement regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
