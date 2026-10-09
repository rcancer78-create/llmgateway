using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMGateway.ChatApp;
using LLMGateway.Server;

namespace LLMGateway.ChatApp.Tests;

public sealed class ChatAppResourceAndConnectionIdentityReviewTests
{
    [Fact]
    public Task ChangingModeWhileReconnectDrainsCannotInstallItsObsoleteApiGateway() => RunStaAsync(async () =>
    {
        var previousAdapter = new OwnedAdapter();
        using var previous = Gateway(previousAdapter, "owned-previous");
        using var replacement = Gateway(new OwnedAdapter(), "owned-replacement");
        const string key = "owned-mode-retirement-key";
        await using var first = await EmbeddedGatewayServer.StartAsync(previous, "http://127.0.0.1:0", new() { ApiKey = key });
        await using var second = await EmbeddedGatewayServer.StartAsync(replacement, "http://127.0.0.1:0", new() { ApiKey = key });
        var model = new MainViewModel(() => previous,
            (service, url, options, token) => EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token))
        { IsApiMode = true, ApiUrl = first.Url, ApiKey = key, AutoRefreshQuotas = false };
        Task? refresh = null;
        Task? reconnect = null;
        try
        {
            Assert.True(model.ConnectCommand.CanExecute(null));
            model.ConnectCommand.Execute(null);
            await PendingCommands(model).WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal("owned-previous", Assert.Single(model.Accounts).Id);
            previousAdapter.HoldNextModels = true;
            model.RefreshCommand.Execute(null);
            refresh = PendingCommands(model);
            await previousAdapter.ModelsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted);
            model.ApiUrl = second.Url;
            Assert.True(model.ConnectCommand.CanExecute(null));
            model.ConnectCommand.Execute(null);
            reconnect = PendingCommands(model);
            Assert.False(reconnect.IsCompleted);
            // Actual enabled mode setter invalidates connection while the admitted reconnect
            // awaits an old authenticated HTTP response. Its captured API must stay obsolete.
            model.IsLibraryMode = true;
            model.Input = "owned mode changed during reconnect";
            Assert.False(model.SendCommand.CanExecute(null));
            previousAdapter.ModelsRelease.TrySetResult();
            Assert.Null(await Record.ExceptionAsync(() => refresh.WaitAsync(TimeSpan.FromSeconds(10))));
            await Record.ExceptionAsync(() => reconnect.WaitAsync(TimeSpan.FromSeconds(10)));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(model.IsLibraryMode);
            Assert.False(model.SendCommand.CanExecute(null));
            Assert.DoesNotContain(model.Accounts, account => account.Id == "owned-replacement");
            Assert.DoesNotContain(model.Models, item => item.AccountId == "owned-replacement");
        }
        finally
        {
            previousAdapter.ModelsRelease.TrySetResult();
            if (refresh is not null) await Record.ExceptionAsync(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));
            if (reconnect is not null) await Record.ExceptionAsync(() => reconnect.WaitAsync(TimeSpan.FromSeconds(10)));
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task ReconnectDoesNotCancelAnAlreadyAdmittedRefreshOnThePreviousAuthenticatedApi() => RunStaAsync(async () =>
    {
        var previousAdapter = new OwnedAdapter();
        using var previous = Gateway(previousAdapter, "owned-previous");
        using var replacement = Gateway(new OwnedAdapter(), "owned-replacement");
        const string key = "owned-refresh-retirement-key";
        await using var first = await EmbeddedGatewayServer.StartAsync(previous, "http://127.0.0.1:0", new() { ApiKey = key });
        await using var second = await EmbeddedGatewayServer.StartAsync(replacement, "http://127.0.0.1:0", new() { ApiKey = key });
        var model = new MainViewModel(() => previous,
            (service, url, options, token) => EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token))
        { IsApiMode = true, ApiUrl = first.Url, ApiKey = key, AutoRefreshQuotas = false };
        Task? refresh = null;
        Task? reconnect = null;
        try
        {
            Assert.True(model.ConnectCommand.CanExecute(null));
            model.ConnectCommand.Execute(null);
            await PendingCommands(model).WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal("owned-previous", Assert.Single(model.Accounts).Id);

            previousAdapter.HoldNextModels = true;
            Assert.True(model.RefreshCommand.CanExecute(null));
            model.RefreshCommand.Execute(null);
            refresh = PendingCommands(model);
            await previousAdapter.ModelsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted);
            Assert.True(model.ConnectCommand.CanExecute(null));

            model.ApiUrl = second.Url;
            model.ConnectCommand.Execute(null);
            reconnect = PendingCommands(model);
            // Both distinct public commands are admitted before completing the actual old HTTP response.
            // Replacing its owner must not turn this successful refresh into a cancellation/error.
            previousAdapter.ModelsRelease.TrySetResult();
            var refreshFailure = await Record.ExceptionAsync(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));
            var reconnectFailure = await Record.ExceptionAsync(() => reconnect.WaitAsync(TimeSpan.FromSeconds(10)));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Null(refreshFailure);
            Assert.Null(reconnectFailure);
            Assert.DoesNotContain(model.Log, line => line.Contains("Ошибка:", StringComparison.Ordinal)
                || line.Contains("Не удалось получить", StringComparison.Ordinal));
            Assert.Equal("owned-replacement", Assert.Single(model.Accounts).Id);
            Assert.All(model.Models, item => Assert.Equal("owned-replacement", item.AccountId));
            Assert.True(model.RefreshCommand.CanExecute(null));
        }
        finally
        {
            previousAdapter.ModelsRelease.TrySetResult();
            if (refresh is not null) await Record.ExceptionAsync(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));
            if (reconnect is not null) await Record.ExceptionAsync(() => reconnect.WaitAsync(TimeSpan.FromSeconds(10)));
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task ACompletedUnrelatedCommandFailureDoesNotLeaveTheActualServerAliveOnShutdown() => RunStaAsync(async () =>
    {
        var adapter = new OwnedAdapter();
        using var gateway = Gateway(adapter, "owned-local");
        EmbeddedGatewayServer? server = null;
        var model = new MainViewModel(() => gateway, async (service, url, options, token) =>
            server = await EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token))
        { EmbeddedUrl = "http://127.0.0.1:0" };
        Task? stopping = null;
        try
        {
            await Invoke(model, "ConnectAsync");
            await Invoke(model, "StartServerAsync");
            Assert.NotNull(server);
            var key = model.ApiKey;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", key);
            using (var before = await http.GetAsync(server.Url + "/v1/gateway/accounts")) Assert.True(before.IsSuccessStatusCode);
            model.LoginCommand.Execute(null);
            await adapter.LoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = model.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            adapter.LoginRelease.TrySetException(new IOException("Owned completed login command failure"));
            var failure = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Null(failure);
            Assert.IsType<HttpRequestException>(await Record.ExceptionAsync(() => http.GetAsync(server.Url + "/v1/gateway/accounts")));
            Assert.False(model.StartServerCommand.CanExecute(null));
            Assert.Null(await Record.ExceptionAsync(() => model.DisposeAsync().AsTask()));
        }
        finally
        {
            adapter.LoginRelease.TrySetException(new IOException("Owned fixture release"));
            if (stopping is not null) await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10)));
            await Record.ExceptionAsync(() => model.DisposeAsync().AsTask());
            if (server is not null) await server.DisposeAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AReplacementPlainApiCannotReuseAccountsOrQuotasFromThePreviousGateway(bool beginWithApi) => RunStaAsync(async () =>
    {
        using var local = Gateway(new OwnedAdapter(), "owned-local");
        using var remote = Gateway(new OwnedAdapter(), "owned-remote");
        const string key = "owned-loopback-key";
        await using var first = await EmbeddedGatewayServer.StartAsync(local, "http://127.0.0.1:0", new() { ApiKey = key });
        await using var second = await EmbeddedGatewayServer.StartAsync(remote, "http://127.0.0.1:0", new() { ApiKey = key, ExposeManagement = false });
        var model = new MainViewModel(() => local,
            (service, url, options, token) => EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token));
        try
        {
            if (beginWithApi)
            {
                model.IsApiMode = true; model.ApiUrl = first.Url; model.ApiKey = key;
            }
            await Invoke(model, "ConnectAsync");
            Assert.Equal("owned-local", Assert.Single(model.Accounts).Id);
            Assert.NotEmpty(model.Quotas); Assert.NotEmpty(model.Providers);
            Assert.Contains("owned-local", model.ModelFilters);

            model.IsApiMode = true; model.ApiUrl = second.Url; model.ApiKey = key;
            await Invoke(model, "ConnectAsync");

            Assert.Empty(model.Accounts); Assert.Empty(model.Quotas); Assert.Empty(model.Providers);
            Assert.Null(model.SelectedAccount);
            Assert.Equal(MainViewModel.AllAccounts, Assert.Single(model.ModelFilters));
            Assert.DoesNotContain(model.Models, m => m.AccountId == "owned-local");
            Assert.False(model.ActivateCommand.CanExecute(null));
            Assert.False(model.LoginCommand.CanExecute(null));
            Assert.False(model.RemoveCommand.CanExecute(null));
        }
        finally { await model.DisposeAsync(); }
    });

    private static LlmGateway Gateway(IProviderAdapter adapter, string id) => new(JsonAccountStore.InMemory(
        [new() { Id = id, DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }]), [adapter],
        new GatewayOptions { WorkspaceDirectory = Path.GetTempPath() });
    private static Task Invoke(MainViewModel model, string name) =>
        (Task)typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
    // Observe the real tasks registered by public command admission, without invoking private command handlers.
    private static Task PendingCommands(MainViewModel model) => Task.WhenAll(
        ((HashSet<Task>)typeof(MainViewModel).GetField("_admittedCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model)!).ToArray());
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
        }) { IsBackground = true, Name = "Owned ChatApp resource identity regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private sealed class OwnedAdapter : IProviderAdapter
    {
        public bool HoldNextModels { get; set; }
        public TaskCompletionSource ModelsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ModelsRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoginEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoginRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile, CancellationToken token)
        {
            if (HoldNextModels)
            {
                HoldNextModels = false;
                ModelsEntered.TrySetResult();
                await ModelsRelease.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return [new("owned-model", "Owned model", true)];
        }
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(QuotaSnapshot.Unsupported(profile, AccountAvailability.Ready, null, "owned", "Owned unsupported quota"));
        public Task StartInteractiveLoginAsync(AccountProfile profile, CancellationToken token)
        { LoginEntered.TrySetResult(); return LoginRelease.Task; }
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; yield return NativeChatEvent.Delta("owned answer"); }
    }
}
