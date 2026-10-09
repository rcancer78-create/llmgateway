using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class CodexTerminalCompletionReviewTests
{
    [Theory]
    [InlineData(false, false, false, 0, false)]
    [InlineData(true, false, false, 0, false)]
    [InlineData(true, true, false, 0, true)]
    [InlineData(false, true, false, 0, true)]
    [InlineData(false, true, true, 0, false)]
    [InlineData(false, true, false, 1, false)]
    public async Task ZeroExitPartialTextRequiresTerminalSuccessEvenAfterReconnect(
        bool reconnectBefore, bool terminal, bool reconnectAfter, int exitCode, bool success)
    {
        using var directory = new TestDirectory();
        const string text = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"owned partial answer\"}}";
        const string retry = "{\"type\":\"error\",\"message\":\"Reconnecting... stream disconnected before completion\"}";
        const string completed = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":2,\"output_tokens\":3}}";
        var frames = new List<string> { text };
        if (reconnectBefore) frames.Add(retry);
        if (terminal) frames.Add(completed);
        if (reconnectAfter) frames.Add(retry);
        var script = directory.GetPath("owned-codex-events.ps1");
        await File.WriteAllTextAsync(script, string.Join("\n", frames.Select(f => "[Console]::WriteLine('"+f+"')"))+"\nexit "+exitCode);
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var launch = new NativeLaunch(new(executable, [], LaunchKind.Direct, executable),
            ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), directory.Root);
        var adapter = new OwnedAdapter(launch);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory(
            [new() { Id="owned", Provider=ProviderKind.Codex, Executable=executable, IsActive=true }]), [adapter],
            new GatewayOptions { DiscoverProfiles=false, WorkspaceDirectory=directory.Root });
        var updates = new List<ChatUpdate>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach(var update in gateway.StreamAsync(new ChatRequest
            { Model="owned-model", AccountId="owned", Messages=[ChatMessage.User("owned input")] }, deadline.Token))
                updates.Add(update);
        });
        Assert.Contains(updates, u=>u.Kind==ChatUpdateKind.TextDelta && u.Text=="owned partial answer");
        if(success)
        {
            Assert.Null(error);
            Assert.Equal("owned partial answer", Assert.Single(updates,u=>u.Kind==ChatUpdateKind.Completed).Result!.Content);
        }
        else
        {
            Assert.Equal(GatewayErrorKind.Upstream, Assert.IsType<GatewayException>(error).Kind);
            Assert.DoesNotContain(updates,u=>u.Kind==ChatUpdateKind.Completed);
        }
    }

    private sealed class OwnedAdapter(NativeLaunch launch) : NativeAdapterBase(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance)
    {
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Owned terminal regression";
        public override string DefaultExecutable => "owned-unused";
        public override ProviderCapabilities Capabilities { get; } = new(false,"owned",MultiAccountSupport.Isolated,"owned",null,null,false,100_000);
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account,CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready,null,"owned",null,DateTimeOffset.UtcNow));
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account,CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("owned-model","Owned model",true)]);
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account,CancellationToken token) =>
            Task.FromResult(QuotaSnapshot.Unsupported(account,AccountAvailability.Ready,null,"owned","owned"));
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account,NativeChatRequest request,CancellationToken token) =>
            StreamAsync(launch,new CodexAdapter.ExecParser(),token);
    }
}
