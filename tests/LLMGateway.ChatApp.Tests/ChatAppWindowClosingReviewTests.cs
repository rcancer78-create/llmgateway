using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using LLMGateway.Core;
using LLMGateway.ChatApp;
using LLMGateway.Server;

namespace LLMGateway.ChatApp.Tests;

/// <summary>Actual shipped Window/VM and owned fake library enumerator; no Application globals or personal diagnostics.</summary>
public sealed class ChatAppWindowClosingReviewTests
{
    [Fact]
    public Task DuplicateCloseKeepsTheActualWindowUntilItsAdmittedChatPhysicallyEnds() => RunStaAsync(async () =>
    {
        var adapter = new DeferredAdapter(false);
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        var events = new List<string>();
        var window = new MainWindow(viewModel, events.Add, LocalResources()) { ShowActivated = false };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        Task? sending = null;
        try
        {
            window.Show();
            await Invoke(viewModel, "ConnectAsync");
            viewModel.Input = "owned pending window chat";
            sending = Invoke(viewModel, "SendAsync");
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            Assert.True(window.IsVisible);
            window.Close();
            Assert.False(closed.Task.IsCompleted, "A duplicate close must remain canceled while the actual chat is unresolved.");
            Assert.True(window.IsVisible);
            Assert.False(adapter.Ended.Task.IsCompleted);
            adapter.Release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(10));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(adapter.Ended.Task.IsCompletedSuccessfully);
            Assert.Contains("MainWindowClosed", events);
        }
        finally
        {
            adapter.Release.TrySetResult();
            if (sending is not null) await Record.ExceptionAsync(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
            await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask());
            if (window.IsVisible) window.Hide();
        }
    });

    [Fact]
    public Task FailedDrainReportsFailureAndAllowsAnExplicitSecondCloseWithoutClaimingSuccessfulDisposal() => RunStaAsync(async () =>
    {
        var adapter = new DeferredAdapter(true);
        using var gateway = OwnedGateway(adapter);
        var viewModel = ViewModel(gateway);
        var events = new List<string>();
        var unhandled = new List<Exception>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherUnhandledExceptionEventHandler observer = (_, args) =>
        {
            unhandled.Add(args.Exception);
            args.Handled = true; // Owned test captures the actual failure without crashing unrelated testhost work.
        };
        dispatcher.UnhandledException += observer;
        var window = new MainWindow(viewModel, events.Add, LocalResources()) { ShowActivated = false };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Task? sending = null;
        try
        {
            window.Show();
            await Invoke(viewModel, "ConnectAsync");
            viewModel.Input = "owned failing window chat";
            sending = Invoke(viewModel, "SendAsync");
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            adapter.Release.TrySetResult();
            Assert.IsType<GatewayException>(await Record.ExceptionAsync(() => sending.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.IsType<GatewayException>(await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask()));
            // Flush the shipped async Closing continuation and its normal-priority queued Close.
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Empty(unhandled);
            Assert.False(closed);
            Assert.True(window.IsVisible);
            Assert.Contains("MainWindowShutdownFailed", events);
            Assert.Contains("не подтверждено", window.Title, StringComparison.Ordinal);
            window.Close();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(closed, "A second explicit close after failed disposal must not trap the user in a disabled window.");
            Assert.Empty(unhandled);
            Assert.Contains("MainWindowClosingAfterShutdownFailure", events);
            Assert.IsType<GatewayException>(await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask()));
        }
        finally
        {
            adapter.Release.TrySetResult();
            if (sending is not null) await Record.ExceptionAsync(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
            await Record.ExceptionAsync(() => viewModel.DisposeAsync().AsTask());
            if (window.IsVisible) window.Hide();
            dispatcher.UnhandledException -= observer;
        }
    });

    private static ResourceDictionary LocalResources()
    {
        // Keep the shipped declarations together in one local resource scope. Compiled external
        // dictionaries normally resolve cross-dictionary brushes through Application.Resources,
        // which these owned lifetime fixtures deliberately do not create or overwrite.
        var styles = Read("Styles");
        var resources = new XElement(styles.Root!);
        resources.AddFirst(Read("Colors").Root!.Elements().Concat(Read("Icons").Root!.Elements())
            .Select(element => new XElement(element)));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        resources.Add(new XElement(presentation + "BooleanToVisibilityConverter",
            new XAttribute(xaml + "Key", "BoolToVisibility")));
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());

        static XDocument Read(string name)
        {
            using var stream = typeof(ChatAppWindowClosingReviewTests).Assembly.GetManifestResourceStream($"OwnedStyles.{name}.xaml")
                ?? throw new InvalidOperationException("Owned shipped-style resource was not embedded.");
            return XDocument.Load(stream);
        }
    }

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
        }) { IsBackground = true, Name = "Owned ChatApp actual window closing regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class DeferredAdapter(bool fail) : IProviderAdapter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                if (fail) throw new GatewayException(GatewayErrorKind.Upstream, "Owned shutdown failure");
                yield return NativeChatEvent.Delta("owned output after release");
            }
            finally { Ended.TrySetResult(); }
        }
    }
}
