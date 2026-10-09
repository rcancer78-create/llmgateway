using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMGateway.ChatApp;
using LLMGateway.Server;

namespace LLMGateway.ChatApp.Tests;

/// <summary>Actual VM/library/owned HTTP contracts, without native providers or personal profile discovery.</summary>
public sealed class ChatAppConnectionAndToolReviewTests
{
    [Fact]
    public Task ChangingTheSelectedTransportRequiresReconnectBeforeAnotherSend() => RunStaAsync(async () =>
    {
        var adapter = new OwnedAdapter();
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        try
        {
            await Invoke(viewModel, "ConnectAsync");
            viewModel.Input = "owned transport change";
            Assert.True(viewModel.SendCommand.CanExecute(null));
            viewModel.IsApiMode = true;
            Assert.False(viewModel.SendCommand.CanExecute(null));
            Assert.IsType<InvalidOperationException>(await Record.ExceptionAsync(() => Invoke(viewModel, "SendAsync")));
            Assert.Equal(0, adapter.ChatCalls);
            Assert.Empty(viewModel.Messages);
        }
        finally { await viewModel.DisposeAsync(); }
    });

    [Fact]
    public Task InvalidReconnectDoesNotDisposeTheExistingWorkingOwnedApiConnection() => RunStaAsync(async () =>
    {
        var adapter = new OwnedAdapter();
        using var gateway = OwnedGateway(adapter);
        const string key = "owned-synthetic-loopback-key";
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new GatewayServerOptions { ApiKey = key, LoopbackOnly = true, SanitizeErrors = true });
        var viewModel = ViewModel(gateway);
        try
        {
            viewModel.IsApiMode = true;
            viewModel.ApiUrl = server.Url;
            viewModel.ApiKey = key;
            viewModel.Stream = false;
            await Invoke(viewModel, "ConnectAsync");
            viewModel.ApiUrl = "http://[";
            Assert.IsType<InvalidOperationException>(await Record.ExceptionAsync(() => Invoke(viewModel, "ConnectAsync")));
            viewModel.Input = "owned prior connection remains usable";
            await Invoke(viewModel, "SendAsync");
            Assert.Equal("owned normal answer", viewModel.Messages[^1].Content);
            Assert.Equal(1, adapter.ChatCalls);
        }
        finally { await viewModel.DisposeAsync(); }
    });

    [Fact]
    public Task FourConsecutiveToolRoundsProduceAnExplicitExhaustionDiagnostic() => RunStaAsync(async () =>
    {
        var adapter = new OwnedAdapter(toolRounds: 4);
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        try
        {
            await Invoke(viewModel, "ConnectAsync");
            viewModel.UseDemoTool = true;
            viewModel.Input = "owned bounded tools";
            await Invoke(viewModel, "SendAsync");
            Assert.Equal(4, adapter.ChatCalls);
            var last = viewModel.Messages[^1];
            Assert.Equal(ChatRole.Assistant, last.Role);
            Assert.True(last.IsError);
            Assert.False(last.IncludeInHistory);
            Assert.Contains("лимит", last.Content, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsChatting);
        }
        finally { await viewModel.DisposeAsync(); }
    });

    [Fact]
    public Task AnEarlierNormalAnswerAfterAToolCallRemainsSuccessful() => RunStaAsync(async () =>
    {
        var adapter = new OwnedAdapter(toolRounds: 1);
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        try
        {
            await Invoke(viewModel, "ConnectAsync");
            viewModel.UseDemoTool = true;
            viewModel.Input = "owned successful tools";
            await Invoke(viewModel, "SendAsync");
            Assert.Equal(2, adapter.ChatCalls);
            Assert.Equal(ChatRole.Assistant, viewModel.Messages[^1].Role);
            Assert.Equal("owned normal answer", viewModel.Messages[^1].Content);
            Assert.False(viewModel.Messages[^1].IsError);
            Assert.True(viewModel.Messages[^1].IncludeInHistory);
        }
        finally { await viewModel.DisposeAsync(); }
    });

    private static MainViewModel ViewModel(LlmGateway gateway) => new(() => gateway,
        (service, url, options, token) => EmbeddedGatewayServer.StartAsync(service, url, options, cancellationToken: token));

    private static LlmGateway OwnedGateway(IProviderAdapter adapter) => new(JsonAccountStore.InMemory(
        [new() { Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }]), [adapter],
        new GatewayOptions { WorkspaceDirectory = Path.GetTempPath() });

    private static Task Invoke(MainViewModel viewModel, string method) =>
        (Task)typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null)!;

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
        }) { IsBackground = true, Name = "Owned ChatApp connection/tool regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class OwnedAdapter(int toolRounds = 0) : IProviderAdapter
    {
        public int ChatCalls { get; private set; }
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("owned-model", "Owned model", true)]);
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(QuotaSnapshot.Unsupported(profile, AccountAvailability.Ready, null, "owned", "Owned unsupported quota"));
        public Task StartInteractiveLoginAsync(AccountProfile profile, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            var round = ++ChatCalls;
            await Task.CompletedTask;
            yield return NativeChatEvent.Delta(round <= toolRounds
                ? "<tool_calls>[{\"name\":\"get_current_time\",\"arguments\":{}}]</tool_calls>"
                : "owned normal answer");
        }
    }
}
