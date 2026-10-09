using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class ProviderTerminalRecordReviewTests
{
    [Theory]
    [InlineData(ProviderKind.Cursor, "missing-terminal", true)]
    [InlineData(ProviderKind.Cursor, "success", false)]
    [InlineData(ProviderKind.Cursor, "plain", false)]
    [InlineData(ProviderKind.Cursor, "nonzero", true)]
    [InlineData(ProviderKind.Grok, "missing-terminal", true)]
    [InlineData(ProviderKind.Grok, "success", false)]
    [InlineData(ProviderKind.Grok, "plain", false)]
    [InlineData(ProviderKind.Grok, "nonzero", true)]
    [InlineData(ProviderKind.Antigravity, "missing-terminal", true)]
    [InlineData(ProviderKind.Antigravity, "success", false)]
    [InlineData(ProviderKind.Antigravity, "plain", false)]
    [InlineData(ProviderKind.Antigravity, "nonzero", true)]
    public async Task GatewayCompletionRequiresStructuredTerminalAndPreservesPlainOutput(ProviderKind provider, string mode, bool fails)
    {
        var directory = Directory.CreateTempSubdirectory("llmgw-provider-terminal-");
        try
        {
            var scriptPath = Path.Combine(directory.FullName, "owned.ps1");
            var script = "[Console]::In.ReadToEnd() | Out-Null;\n";
            if (mode == "plain") script += "[Console]::Out.WriteLine('owned complete plain answer');\n";
            else
            {
                script += Print(Delta(provider));
                if (mode == "success") script += Print(Terminal(provider));
            }
            if (mode == "nonzero") script += "exit 7;\n";
            await File.WriteAllTextAsync(scriptPath, script);
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var launch = new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["-NoProfile", "-NonInteractive", "-File", scriptPath], new Dictionary<string, string?>(), directory.FullName, "owned fixture input");
            var parser = provider switch
            {
                ProviderKind.Cursor => (IChatLineParser)new CursorAdapter.StreamJsonParser(),
                ProviderKind.Grok => new GrokAdapter.StreamingJsonParser(),
                _ => new AntigravityAdapter.StreamJsonParser()
            };
            var profile = new AccountProfile { Id = "owned", DisplayName = "Owned replay", Provider = provider, IsActive = true,
                Executable = shell, WorkingDirectory = directory.FullName };
            using var gateway = new LlmGateway(JsonAccountStore.InMemory([profile]), [new ReplayAdapter(provider, launch, parser)],
                new GatewayOptions { WorkspaceDirectory = directory.FullName, DiscoverProfiles = false, RequestTimeoutSeconds = 30 });
            var model = Assert.Single(await gateway.GetModelsAsync()).Id;
            var request = new ChatRequest { Model = model, Messages = [ChatMessage.User("Owned fixture input")] };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (fails)
            {
                var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(request, timeout.Token));
                Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
            }
            else
            {
                var answer = await gateway.CompleteAsync(request, timeout.Token);
                Assert.Equal(mode == "plain" ? "owned complete plain answer" : "owned partial answer", answer.Content);
                Assert.Equal("stop", answer.FinishReason);
            }
        }
        finally { directory.Delete(true); }
    }

    private static string Print(string json) => "[Console]::Out.WriteLine('" + json.Replace("'", "''") + "');\n";
    private static string Delta(ProviderKind provider) => provider switch
    {
        ProviderKind.Cursor => JsonSerializer.Serialize(new { type = "assistant", timestamp_ms = 1, message = new { content = new[] { new { type = "text", text = "owned partial answer" } } } }),
        ProviderKind.Grok => JsonSerializer.Serialize(new { type = "text", data = "owned partial answer" }),
        _ => JsonSerializer.Serialize(new { @event = "step_update", step_update = new { step_type = "agent_response", text_delta = "owned partial answer" } })
    };
    private static string Terminal(ProviderKind provider) => provider switch
    {
        ProviderKind.Cursor => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = "owned partial answer" }),
        ProviderKind.Grok => JsonSerializer.Serialize(new { type = "end", stopReason = "stop" }),
        _ => JsonSerializer.Serialize(new { @event = "result", result = new { status = "SUCCESS", response = "owned partial answer" } })
    };

    private sealed class ReplayAdapter(ProviderKind provider, NativeLaunch launch, IChatLineParser parser)
        : NativeAdapterBase(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance)
    {
        public override ProviderKind Provider => provider;
        public override string DisplayName => "Owned terminal replay";
        public override string DefaultExecutable => "owned-fixture";
        public override ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, false, 100_000);
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Owned", true)]);
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => StreamAsync(launch, parser, token);
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
    }
}
