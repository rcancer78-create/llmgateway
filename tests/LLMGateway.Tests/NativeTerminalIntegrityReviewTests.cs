using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class NativeTerminalIntegrityReviewTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("error_max_turns", true)]
    [InlineData("success", false)]
    [InlineData("", false)]
    public async Task ClaudePartialOutputRequiresSuccessfulTerminalResult(string? subtype, bool fails)
    {
        var script = "[Console]::Out.WriteLine('{\"type\":\"stream_event\",\"event\":{\"delta\":{\"type\":\"text_delta\",\"text\":\"owned\"}}}')";
        if (subtype is not null)
            script += subtype.Length == 0
                ? "; [Console]::Out.WriteLine('{\"type\":\"result\",\"is_error\":false}')"
                : "; [Console]::Out.WriteLine('{\"type\":\"result\",\"subtype\":\"" + subtype + "\",\"is_error\":false}')";
        var events = await RunAsync(script, new LLMGateway.Native.Adapters.ClaudeAdapter.StreamJsonParser());
        Assert.Equal("owned", Assert.Single(events, e => e.Kind == NativeChatEventKind.Text).Text);
        Assert.Equal(fails, events.Any(e => e.Kind == NativeChatEventKind.Error));
    }

    [Fact]
    public async Task ClaudePlainOutputFallbackRemainsSupported()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('owned plain answer')", new LLMGateway.Native.Adapters.ClaudeAdapter.StreamJsonParser());
        Assert.Equal("owned plain answer", Assert.Single(events).Text);
        Assert.Equal(NativeChatEventKind.FinalText, events[0].Kind);
    }

    [Theory]
    [InlineData("The expected JSON is {\"answer\":42}")]
    [InlineData("An empty object is {}")]
    [InlineData("Example: {\"type\":\"result\",\"subtype\":\"success\",\"result\":\"not the whole answer\"}")]
    public async Task PlainAnswerContainingJsonExampleRetainsItsCompleteText(string answer)
    {
        var script = "[Console]::Out.WriteLine('" + answer.Replace("'", "''") + "')";
        var events = await RunAsync(script, new LLMGateway.Native.Adapters.ClaudeAdapter.StreamJsonParser());
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.Error);
        Assert.Equal(answer, Assert.Single(events, e => e.Kind == NativeChatEventKind.FinalText).Text);
    }

    [Fact]
    public async Task IndentedStructuredTerminalRemainsProtocolOutput()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('  {\"type\":\"result\",\"subtype\":\"success\",\"result\":\"owned indented answer\"}')",
            new LLMGateway.Native.Adapters.ClaudeAdapter.StreamJsonParser());
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.Error);
        Assert.Equal("owned indented answer", Assert.Single(events, e => e.Kind == NativeChatEventKind.FinalText).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleLargePlainLinesRetainTheCompleteAnswer(bool invalidJson)
    {
        var prefix = invalidJson ? "{invalid" : "owned";
        var line = prefix + new string('x', 20_000);
        var events = await RunAsync("1..3 | ForEach-Object { [Console]::Out.WriteLine('" + line + "') }", new Parser());
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.Error);
        Assert.Equal(string.Join(Environment.NewLine, Enumerable.Repeat(line, 3)),
            Assert.Single(events, e => e.Kind == NativeChatEventKind.FinalText).Text);
    }

    [Fact]
    public async Task AggregatePlainOutputBeyondCommandBoundFailsInsteadOfReturningAPrefix()
    {
        var events = await RunAsync("1..220 | ForEach-Object { [Console]::Out.WriteLine(('x' * 20000)) }", new Parser());
        Assert.Single(events, e => e.Kind == NativeChatEventKind.Error);
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.FinalText);
    }

    [Fact]
    public async Task CompletionOnlyAnswerHasOneSuccessfulTerminalEvent()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('{}')", new Parser(complete: true));
        Assert.Equal("owned completion", Assert.Single(events).Text);
        Assert.Equal(NativeChatEventKind.FinalText, events[0].Kind);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("format")]
    public async Task MalformedStructuredEventAfterDeltaCannotBecomeSuccess(string shape)
    {
        var events = await RunAsync("[Console]::Out.WriteLine('{\"text\":true}'); [Console]::Out.WriteLine('{\"bad\":true}')", new Parser(shape));
        Assert.Equal(NativeChatEventKind.Text, events[0].Kind);
        Assert.Equal(NativeChatEventKind.Error, events[^1].Kind);
        Assert.Single(events, e => e.Kind == NativeChatEventKind.Error);
    }

    [Fact]
    public async Task StructuredNoiseWithoutAnswerStillFails()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('diagnostic'); [Console]::Out.WriteLine('{}')", new Parser());
        Assert.Equal(NativeChatEventKind.Error, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task StructuredAnswerDoesNotReturnDiagnosticPlainLines()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('diagnostic'); [Console]::Out.WriteLine('{\"text\":true}')", new Parser());
        Assert.Equal(NativeChatEventKind.Text, Assert.Single(events).Kind);
        Assert.Equal("owned delta", events[0].Text);
    }

    [Fact]
    public async Task NonzeroExitAfterDeltaStillFails()
    {
        var events = await RunAsync("[Console]::Out.WriteLine('{\"text\":true}'); exit 7", new Parser());
        Assert.Equal(NativeChatEventKind.Text, events[0].Kind);
        Assert.Equal(NativeChatEventKind.Error, events[^1].Kind);
    }

    private static async Task<List<NativeChatEvent>> RunAsync(string script, IChatLineParser parser)
    {
        var directory = Directory.CreateTempSubdirectory("llmgw-owned-terminal-");
        try
        {
            var file = Path.Combine(directory.FullName, "fixture.ps1");
            await File.WriteAllTextAsync(file, script);
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var launch = new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["-NoProfile", "-NonInteractive", "-File", file], new Dictionary<string, string?>(), directory.FullName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = new List<NativeChatEvent>();
            await foreach (var e in new Adapter().Read(launch, parser, timeout.Token)) result.Add(e);
            return result;
        }
        finally { directory.Delete(true); }
    }

    private sealed class Parser(string? shape = null, bool complete = false) : IChatLineParser
    {
        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            if (line.TryGetProperty("bad", out _)) throw shape switch
            {
                "missing" => new KeyNotFoundException("owned fixture"),
                "format" => new FormatException("owned fixture"),
                _ => new InvalidOperationException("owned fixture")
            };
            return line.TryGetProperty("text", out _) ? [NativeChatEvent.Delta("owned delta")] : [];
        }
        public IEnumerable<NativeChatEvent> Complete() => complete ? [NativeChatEvent.Final("owned completion")] : [];
    }

    private sealed class Adapter() : NativeAdapterBase(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance)
    {
        public IAsyncEnumerable<NativeChatEvent> Read(NativeLaunch launch, IChatLineParser parser, CancellationToken token)
            => StreamAsync(launch, parser, token);
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Owned terminal fixture";
        public override string DefaultExecutable => "owned-fixture";
        public override ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, false, 1000);
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => throw new NotSupportedException();
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
    }
}
