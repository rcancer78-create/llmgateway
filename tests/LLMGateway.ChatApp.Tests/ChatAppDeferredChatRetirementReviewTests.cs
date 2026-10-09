using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMGateway.ChatApp;
using LLMGateway.Server;

namespace LLMGateway.ChatApp.Tests;

/// <summary>Actual library/enumerator lifetime with an owned fake adapter; establishes no native ownership proof.</summary>
public sealed class ChatAppDeferredChatRetirementReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetirementJoinsAnIgnoringChatAndDoesNotPublishItsLateResult(bool checkPublication) => RunStaAsync(async () =>
    {
        var adapter = new DeferredAdapter();
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        Task? sending = null;
        Task? stopping = null;
        try
        {
            await Invoke(viewModel, "ConnectAsync");
            viewModel.Input = "owned deferred chat";
            sending = Invoke(viewModel, "SendAsync");
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var assistant = viewModel.Messages[^1];
            var beforeContent = assistant.Content;
            var beforeFooter = assistant.Footer;
            stopping = viewModel.DisposeAsync().AsTask();
            Assert.True(adapter.ChatToken.IsCancellationRequested);
            if (!checkPublication)
                Assert.False(stopping.IsCompleted, "Disposal must retain the admitted chat until its actual enumerator finishes.");
            adapter.Release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(10));
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(adapter.Ended.Task.IsCompletedSuccessfully);
            if (checkPublication)
            {
                Assert.Equal(beforeContent, assistant.Content);
                Assert.Equal(beforeFooter, assistant.Footer);
            }
        }
        finally
        {
            adapter.Release.TrySetResult();
            if (sending is not null) await Record.ExceptionAsync(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
            if (stopping is not null) await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10)));
            await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public Task AdmittedChatNormallyPublishesItsResultBeforeRetirement() => RunStaAsync(async () =>
    {
        var adapter = new DeferredAdapter();
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        try
        {
            await Invoke(viewModel, "ConnectAsync");
            viewModel.Input = "owned normal chat";
            var sending = Invoke(viewModel, "SendAsync");
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            adapter.Release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("owned actual late output", viewModel.Messages[^1].Content);
            Assert.True(adapter.Ended.Task.IsCompletedSuccessfully);
        }
        finally { adapter.Release.TrySetResult(); await viewModel.DisposeAsync(); }
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
        }) { IsBackground = true, Name = "Owned ChatApp deferred chat regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class DeferredAdapter : IProviderAdapter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ChatToken { get; private set; }
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
            ChatToken = token;
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                yield return NativeChatEvent.Delta("owned actual late output");
            }
            finally { Ended.TrySetResult(); }
        }
    }
}
