using System.Text.Json;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native.Adapters;

/// <summary>
/// Anthropic Claude Code CLI (<c>claude</c>), prepared for when it is installed. Chat:
/// <c>-p --output-format stream-json --verbose --include-partial-messages</c> with the prompt on stdin.
/// Accounts are isolated with <c>CLAUDE_CONFIG_DIR</c>. The CLI reports usage only interactively (<c>/usage</c>).
/// </summary>
public sealed class ClaudeAdapter : NativeAdapterBase
{
    private static readonly NativeModel[] Aliases =
    [
        new("sonnet", "Claude Sonnet (последняя)", true),
        new("opus", "Claude Opus (последняя)"),
        new("haiku", "Claude Haiku (последняя)")
    ];

    public ClaudeAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<ClaudeAdapter> logger) : base(resolver, options, logger)
    {
    }

    public override ProviderKind Provider => ProviderKind.Claude;
    public override string DisplayName => "Claude Code";
    public override string DefaultExecutable => "claude";

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsQuota: false,
        QuotaSource: "Claude Code показывает использование только в интерактивном /usage",
        MultiAccount: MultiAccountSupport.Isolated,
        MultiAccountNotes: "Каждый аккаунт — отдельный каталог CLAUDE_CONFIG_DIR (штатный механизм Claude Code). Вход — «Войти» и /login в окне claude.",
        ConfigDirectoryVariable: "CLAUDE_CONFIG_DIR",
        ApiKeyTargetVariable: "ANTHROPIC_API_KEY",
        SupportsReasoningEffort: false,
        MaxPromptCharacters: 1_000_000);

    public override IEnumerable<AccountProfile> DiscoverProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured ? configured : Path.Combine(home, ".claude");
        return DiscoverHomeDirectories(".claude", defaultDirectory, ["settings.json", ".credentials.json"], "claude-profile-");
    }

    protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => [];

    public override async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["--version"], cancellationToken).ConfigureAwait(false);
        if (!result.Success) return StatusFromFailure(result);
        var version = Lines(result.StandardOutput).FirstOrDefault();
        return new AccountStatus(AccountAvailability.Unknown, null, version, "Claude Code установлен; вход проверяется при первом запросе.", DateTimeOffset.UtcNow);
    }

    public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NativeModel>>(Aliases);

    public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(account, cancellationToken).ConfigureAwait(false);
        return QuotaSnapshot.Unsupported(account, status.Availability, null, "claude", "Claude Code не публикует лимиты вне интерактивного /usage; исчерпание фиксируется по ответам клиента.");
    }

    public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        var arguments = BuildChatArguments(account, request);
        return StreamAsync(Launch(account, arguments, request.WorkingDirectory, request.Prompt), new StreamJsonParser(), cancellationToken);
    }

    internal static List<string> BuildChatArguments(AccountProfile account, NativeChatRequest request)
    {
        var arguments = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--max-turns", "1", "--permission-mode", "plan", "--strict-mcp-config", "--tools", ""
        };
        if (ModelArgument(request.Model) is { } model) arguments.AddRange(["--model", model]);
        arguments.AddRange(account.ExtraArguments);
        return arguments;
    }

    internal sealed class StreamJsonParser : IChatLineParser
    {
        private bool _sawDelta;
        private bool _sawStructuredEvent;
        private bool _sawResult;

        public IEnumerable<NativeChatEvent> Complete()
        {
            if (_sawStructuredEvent && !_sawResult)
                yield return Failure("Claude Code завершился без итогового result.");
        }

        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            _sawStructuredEvent = true;
            switch (line.Str("type"))
            {
                case "stream_event":
                    var delta = line.Prop("event").Prop("delta");
                    switch (delta.Str("type"))
                    {
                        case "text_delta" when delta.Str("text") is { Length: > 0 } text:
                            _sawDelta = true;
                            yield return NativeChatEvent.Delta(text);
                            break;
                        case "thinking_delta" when delta.Str("thinking") is { Length: > 0 } thinking:
                            yield return new NativeChatEvent(NativeChatEventKind.Reasoning, thinking);
                            break;
                    }
                    break;
                case "assistant" when !_sawDelta:
                    var full = line.Prop("message").ContentText();
                    if (full.Length > 0) yield return NativeChatEvent.Final(full);
                    break;
                case "result":
                    _sawResult = true;
                    if (line.Bool("is_error") || line.Str("subtype") is { } subtype && subtype != "success")
                    {
                        yield return Failure(line.Str("result") ?? "Claude Code: запрос завершился ошибкой.", line.Prop("error"));
                        yield break;
                    }
                    var usage = line.Prop("usage");
                    if (usage is not null)
                    {
                        var cached = usage.Int("cache_read_input_tokens");
                        var input = usage.Int("input_tokens") + cached + usage.Int("cache_creation_input_tokens");
                        var output = usage.Int("output_tokens");
                        yield return NativeChatEvent.Reported(new TokenUsage(input, output, input + output, cached));
                    }
                    if (!_sawDelta && line.Str("result") is { Length: > 0 } result) yield return NativeChatEvent.Final(result);
                    break;
            }
        }
    }
}
