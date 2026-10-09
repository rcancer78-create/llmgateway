using System.Text.Json;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native.Adapters;

/// <summary>
/// Cursor Agent CLI. Chat: <c>--print --output-format stream-json --mode ask</c> (read-only).
/// Status/plan: <c>status --format json</c> and <c>about --format json</c>. The CLI exposes usage only
/// in its interactive <c>/usage</c> screen, so remaining limits are not available non-interactively.
/// </summary>
public sealed class CursorAdapter : NativeAdapterBase
{
    public CursorAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<CursorAdapter> logger) : base(resolver, options, logger)
    {
    }

    public override ProviderKind Provider => ProviderKind.Cursor;
    public override string DisplayName => "Cursor";
    public override string DefaultExecutable => "cursor-agent";

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsQuota: false,
        QuotaSource: "cursor-agent about/status: тариф и вход; остаток лимитов CLI показывает только в интерактивном /usage",
        MultiAccount: MultiAccountSupport.Single,
        MultiAccountNotes: "Cursor CLI хранит один вход на пользователя ОС. Смена аккаунта — штатные agent logout / agent login.",
        ConfigDirectoryVariable: null,
        ApiKeyTargetVariable: "CURSOR_API_KEY",
        SupportsReasoningEffort: false,
        MaxPromptCharacters: 1_000_000);

    protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => ["login"];

    public override async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        using var commands = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<NativeRunResult>? statusTask = null;
        Task<NativeRunResult>? aboutTask = null;
        try
        {
            statusTask = RunAsync(account, ["status", "--format", "json"], commands.Token);
            aboutTask = RunAsync(account, ["about", "--format", "json"], commands.Token);
            // Observe the first failure promptly, then join both RunAsync cleanup owners.
            var first = await Task.WhenAny(statusTask, aboutTask).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await Task.WhenAll(statusTask, aboutTask).ConfigureAwait(false);
        }
        catch
        {
            commands.Cancel();
            // Includes synchronous launch failure after only the first command was started.
            if (statusTask is not null)
                try { await statusTask.ConfigureAwait(false); } catch (Exception) { }
            if (aboutTask is not null)
                try { await aboutTask.ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        var status = await statusTask.ConfigureAwait(false);
        var about = await aboutTask.ConfigureAwait(false);
        var aboutJson = TryJson(about.StandardOutput);
        var version = aboutJson.Str("cliVersion");
        var statusJson = TryJson(status.StandardOutput);
        if (!status.Success || statusJson is null) return StatusFromFailure(status, version);
        if (!statusJson.Value.Bool("isAuthenticated"))
            return new AccountStatus(AccountAvailability.AuthenticationRequired, null, version, "Cursor CLI не авторизован. Нажмите «Войти».", DateTimeOffset.UtcNow);
        var user = statusJson.Prop("userInfo");
        var identity = new AccountIdentity(user.Str("email") ?? aboutJson.Str("userEmail"), aboutJson.Str("subscriptionTier"), user.Num("userId")?.ToString("0"));
        return new AccountStatus(AccountAvailability.Ready, identity, version, null, DateTimeOffset.UtcNow);
    }

    public override async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["--list-models"], cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseModels(result.StandardOutput) : [];
    }

    public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(account, cancellationToken).ConfigureAwait(false);
        var message = status.Availability == AccountAvailability.Ready
            ? "Cursor CLI не публикует остаток лимитов вне интерактивного режима (команда /usage). Показан тариф."
            : status.Message ?? "Статус Cursor неизвестен.";
        return QuotaSnapshot.Unsupported(account, status.Availability, status.Identity?.Plan, "cursor-agent about/status", message);
    }

    public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        var arguments = BuildChatArguments(account, request);
        // No positional prompt: with a non-TTY stdin the CLI reads the prompt until EOF, so it never
        // lands on the Windows command line (32,767-character limit and quote re-parsing).
        return StreamAsync(Launch(account, arguments, request.WorkingDirectory, request.Prompt), new StreamJsonParser(), cancellationToken);
    }

    internal static List<string> BuildChatArguments(AccountProfile account, NativeChatRequest request)
    {
        // --trust would load MCP servers and rules from the working directory. Headless runs stay untrusted.
        var arguments = new List<string> { "--print", "--mode", "ask", "--output-format", "stream-json", "--stream-partial-output" };
        if (ModelArgument(request.Model) is { } model) arguments.AddRange(["--model", model]);
        arguments.AddRange(account.ExtraArguments);
        return arguments;
    }

    internal static IReadOnlyList<NativeModel> ParseModels(string output)
    {
        var models = new List<NativeModel>();
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            var id = line[..separator].Trim();
            if (!ModelRouter.IsValidModelName(id)) continue;
            var name = line[(separator + 3)..].Trim();
            var isDefault = name.Contains("default", StringComparison.OrdinalIgnoreCase);
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            models.Add(new NativeModel(id, paren > 0 ? name[..paren] : name, isDefault));
        }
        return models;
    }

    private static JsonElement? TryJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed class StreamJsonParser : IChatLineParser
    {
        private bool _sawStructuredEvent;
        private bool _sawTerminal;

        public IEnumerable<NativeChatEvent> Complete()
        {
            if (_sawStructuredEvent && !_sawTerminal)
                yield return Failure("Cursor завершился без итогового result.");
        }

        private bool _sawDelta;

        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            var eventType = line.Str("type");
            if (eventType is "assistant" or "thinking" or "result" or "error") _sawStructuredEvent = true;
            switch (eventType)
            {
                case "assistant":
                    var text = line.Prop("message").ContentText();
                    if (text.Length == 0) yield break;
                    // Partial deltas carry timestamp_ms; copies with model_call_id (before tool calls) or
                    // without timestamp_ms (end of turn) repeat text that was already streamed.
                    if (line.Prop("model_call_id") is not null) yield break;
                    if (line.Prop("timestamp_ms") is not null)
                    {
                        _sawDelta = true;
                        yield return NativeChatEvent.Delta(text);
                    }
                    else if (!_sawDelta)
                    {
                        yield return NativeChatEvent.Final(text);
                    }
                    break;
                case "thinking":
                    if (line.Str("text") is { Length: > 0 } thinking) yield return new NativeChatEvent(NativeChatEventKind.Reasoning, thinking);
                    break;
                case "result":
                    _sawTerminal = true;
                    if (line.Bool("is_error") || line.Str("subtype") is { } subtype && subtype != "success")
                    {
                        yield return Failure(line.Str("result") ?? line.Str("error") ?? "Cursor: запрос завершился ошибкой.", line.Prop("error"));
                        yield break;
                    }
                    var usage = line.Prop("usage");
                    if (usage is not null)
                    {
                        var cached = usage.Int("cacheReadTokens", "cache_read_tokens");
                        var input = usage.Int("inputTokens", "input_tokens") + cached + usage.Int("cacheWriteTokens", "cache_write_tokens");
                        var output = usage.Int("outputTokens", "output_tokens");
                        yield return NativeChatEvent.Reported(new TokenUsage(input, output, input + output, cached));
                    }
                    if (!_sawDelta && line.Str("result") is { Length: > 0 } result) yield return NativeChatEvent.Final(result);
                    break;
                case "error":
                    yield return Failure(line.Str("message") ?? line.Prop("error").Str("message") ?? "Cursor: ошибка.", line.Prop("error"));
                    break;
            }
        }
    }
}
