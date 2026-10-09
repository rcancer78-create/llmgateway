using System.Text.Json;
using System.Text.RegularExpressions;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native.Adapters;

/// <summary>
/// OpenAI Codex CLI. Chat: <c>codex exec --json</c> (read-only sandbox, no approvals). Account, models and
/// rate limits: the official <c>codex app-server</c> JSON-RPC (<c>account/read</c>, <c>model/list</c>, <c>account/rateLimits/read</c>).
/// Several accounts are isolated with separate <c>CODEX_HOME</c> directories.
/// </summary>
public sealed partial class CodexAdapter : NativeAdapterBase
{
    public CodexAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<CodexAdapter> logger) : base(resolver, options, logger)
    {
    }

    public override ProviderKind Provider => ProviderKind.Codex;
    public override string DisplayName => "Codex";
    public override string DefaultExecutable => "codex";

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsQuota: true,
        QuotaSource: "codex app-server · account/rateLimits/read",
        MultiAccount: MultiAccountSupport.Isolated,
        MultiAccountNotes: "Каждый аккаунт — отдельный каталог CODEX_HOME (штатный механизм Codex CLI). Вход: «Войти» → codex login в этом каталоге.",
        ConfigDirectoryVariable: "CODEX_HOME",
        ApiKeyTargetVariable: "CODEX_API_KEY",
        SupportsReasoningEffort: true,
        MaxPromptCharacters: 1_000_000);

    public override IEnumerable<AccountProfile> DiscoverProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured ? configured : Path.Combine(home, ".codex");
        return DiscoverHomeDirectories(".codex", defaultHome, ["config.toml", "auth.json"], "codex-profile-");
    }

    protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => ["login"];

    public override async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(account, experimentalApi: false, cancellationToken).ConfigureAwait(false);
        var result = await session.Rpc.RequestAsync("account/read", new { refreshToken = false }, ShortTimeout, cancellationToken).ConfigureAwait(false);
        return ToStatus(result, session.Version);
    }

    public override async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(account, experimentalApi: false, cancellationToken).ConfigureAwait(false);
        var models = new List<NativeModel>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await session.Rpc.RequestAsync("model/list", cursor is null ? new { } : new { cursor }, ShortTimeout, cancellationToken).ConfigureAwait(false);
            models.AddRange(ParseModels(result));
            cursor = result.Str("nextCursor");
            if (cursor is null) break;
        }
        if (cursor is not null)
            throw new GatewayException(GatewayErrorKind.Upstream, "Codex model catalog discovery exceeded the page limit before completion.");
        return models;
    }

    public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(account, experimentalApi: true, cancellationToken).ConfigureAwait(false);
        var accountInfo = await session.Rpc.RequestAsync("account/read", new { refreshToken = false }, ShortTimeout, cancellationToken).ConfigureAwait(false);
        var status = ToStatus(accountInfo, session.Version);
        if (status.Availability == AccountAvailability.AuthenticationRequired)
            return QuotaSnapshot.Unsupported(account, status.Availability, null, Capabilities.QuotaSource, status.Message ?? "Нужен вход в Codex.") with { Supported = true };
        JsonElement limits;
        try
        {
            limits = await session.Rpc.RequestAsync("account/rateLimits/read", null, ShortTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex) when (RpcErrors.IsMissingMethod(ex))
        {
            return QuotaSnapshot.Unsupported(account, status.Availability, status.Identity?.Plan, Capabilities.QuotaSource,
                "Эта сборка Codex не отдала account/rateLimits/read. Квоты появятся, когда метод снова доступен в app-server.");
        }
        return ParseQuota(account, limits, status.Identity?.Plan);
    }

    public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        var arguments = BuildChatArguments(account, request);
        return StreamAsync(Launch(account, arguments, request.WorkingDirectory, request.Prompt), new ExecParser(), cancellationToken);
    }

    internal static List<string> BuildChatArguments(AccountProfile account, NativeChatRequest request)
    {
        var arguments = new List<string> { "-a", "never", "exec", "--json", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never" };
        if (ModelArgument(request.Model) is { } model) arguments.AddRange(["-m", model]);
        if (Effort(request.ReasoningEffort) is { } effort) arguments.AddRange(["-c", $"model_reasoning_effort={effort}"]);
        arguments.AddRange(["-c", "mcp_servers={}"]);
        arguments.AddRange(account.ExtraArguments);
        arguments.Add("-");
        return arguments;
    }

    internal static QuotaSnapshot ParseQuota(AccountProfile account, JsonElement result, string? plan)
    {
        var buckets = new List<QuotaBucket>();
        var byId = result.Prop("rateLimitsByLimitId");
        var limitSets = byId is { ValueKind: JsonValueKind.Object } map
            ? map.EnumerateObject().Select(p => p.Value).ToList()
            : result.Prop("rateLimits") is { } single ? [single] : [];
        var reached = false;
        foreach (var set in limitSets)
        {
            var limitId = set.Str("limitId") ?? "codex";
            var limitName = set.Str("limitName");
            plan ??= set.Str("planType");
            reached |= set.Str("rateLimitReachedType") is not null || set.Bool("spendControlReached");
            foreach (var window in new[] { "primary", "secondary" })
            {
                if (set.Prop(window) is not { } bucket) continue;
                var minutes = (int?)bucket.Num("windowDurationMins");
                var prefix = limitSets.Count > 1 || limitId != "codex" ? (limitName ?? limitId) + " · " : string.Empty;
                buckets.Add(new QuotaBucket($"{limitId}.{window}", prefix + WindowLabel(minutes, window), bucket.Num("usedPercent"),
                    WindowMinutes: minutes, ResetsAt: bucket.Date("resetsAt"), Unit: "%"));
            }
            if (set.Prop("individualLimit") is { } spend && spend.Num("limit") is > 0 and var spendLimit)
            {
                var used = spend.Num("used") ?? spendLimit * (100 - (spend.Num("remainingPercent") ?? 100)) / 100;
                buckets.Add(new QuotaBucket($"{limitId}.individual", "Лимит рабочего пространства", used / spendLimit * 100,
                    Used: used, Limit: spendLimit, Unit: "credits", ResetsAt: spend.Date("resetsAt")));
            }
            if (set.Prop("credits") is { } credits && credits.Bool("hasCredits") && credits.Num("balance") is { } balance)
                buckets.Add(new QuotaBucket($"{limitId}.credits", "Кредиты", null, Used: 0, Limit: balance, Unit: "credits"));
        }
        var availability = reached ? AccountAvailability.RateLimited : AccountAvailability.Ready;
        var message = reached ? "Codex сообщает, что лимит исчерпан." : buckets.Count == 0 ? "Codex не вернул окон лимитов." : null;
        return new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, availability, true, plan, buckets, "codex app-server · account/rateLimits/read", message);
    }

    private static string WindowLabel(int? minutes, string window) => minutes switch
    {
        300 => "5 часов",
        1440 => "Сутки",
        10080 => "Неделя",
        { } m when m % 1440 == 0 => $"{m / 1440} дн.",
        { } m when m % 60 == 0 => $"{m / 60} ч",
        { } m => $"{m} мин",
        null => window == "primary" ? "Основной лимит" : "Дополнительный лимит"
    };

    internal static AccountStatus ToStatus(JsonElement result, string? version)
    {
        var account = result.Prop("account");
        if (account is null)
        {
            var requires = result.Bool("requiresOpenaiAuth");
            return new AccountStatus(requires ? AccountAvailability.AuthenticationRequired : AccountAvailability.Unknown, null, version,
                requires ? "Codex не авторизован в этом CODEX_HOME. Нажмите «Войти»." : null, DateTimeOffset.UtcNow);
        }
        var identity = new AccountIdentity(account.Str("email"), account.Str("planType") ?? account.Str("type"), null);
        return new AccountStatus(AccountAvailability.Ready, identity, version, null, DateTimeOffset.UtcNow);
    }

    private static IEnumerable<NativeModel> ParseModels(JsonElement result)
    {
        foreach (var item in result.Prop("data").Items())
        {
            if (item.Bool("hidden")) continue;
            var id = item.Str("model") ?? item.Str("id");
            if (id is null || !ModelRouter.IsValidModelName(id)) continue;
            yield return new NativeModel(id, item.Str("displayName") ?? id, item.Bool("isDefault"));
        }
    }

    private async Task<AppServerSession> OpenAsync(AccountProfile account, bool experimentalApi, CancellationToken cancellationToken)
    {
        var rpc = JsonRpcStdioClient.StartCodexAppServer(Launch(account, ["app-server"]));
        try
        {
            // account/rateLimits/read is gated by experimentalApi. Account and model calls stay on the stable handshake.
            var capabilities = new Dictionary<string, object>();
            if (experimentalApi) capabilities["experimentalApi"] = true;
            var init = await rpc.RequestAsync("initialize", new
            {
                clientInfo = new { name = "llmgateway", title = "LLMGateway", version = "2.0" },
                capabilities
            }, ShortTimeout, cancellationToken).ConfigureAwait(false);
            await rpc.NotifyAsync("initialized", null, cancellationToken).ConfigureAwait(false);
            var agent = init.Str("userAgent");
            var version = agent is null ? null : VersionPattern().Match(agent) is { Success: true } match ? match.Groups[1].Value : null;
            return new AppServerSession(rpc, version);
        }
        catch
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [GeneratedRegex(@"/(\d+\.\d+\.\d+[\w.-]*)")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"reconnect|retrying|stream disconnected", RegexOptions.IgnoreCase)]
    private static partial Regex TransientPattern();

    private sealed record AppServerSession(JsonRpcStdioClient Rpc, string? Version) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Rpc.DisposeAsync();
    }

    internal sealed class ExecParser : IChatLineParser
    {
        private bool _hasText;
        private bool _turnCompleted;
        private bool _failed;

        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            switch (line.Str("type"))
            {
                case "item.completed":
                    var item = line.Prop("item");
                    var text = item.Str("text");
                    switch (item.Str("type"))
                    {
                        case "agent_message" when !string.IsNullOrEmpty(text):
                            yield return NativeChatEvent.Delta((_hasText ? "\n\n" : string.Empty) + text);
                            _hasText = true;
                            break;
                        case "reasoning" when !string.IsNullOrEmpty(text):
                            yield return new NativeChatEvent(NativeChatEventKind.Reasoning, text);
                            break;
                    }
                    break;
                case "turn.completed":
                    _turnCompleted = true;
                    var usage = line.Prop("usage");
                    var input = usage.Int("input_tokens");
                    var output = usage.Int("output_tokens");
                    yield return NativeChatEvent.Reported(new TokenUsage(input, output, input + output, usage.Int("cached_input_tokens"), usage.Int("reasoning_output_tokens")));
                    break;
                case "turn.failed":
                    _failed = true;
                    var failed = line.Prop("error");
                    yield return Failure(failed.Str("message") ?? "Codex: ход завершился ошибкой.", failed);
                    break;
                case "error" when line.Str("message") is not { } message || !TransientPattern().IsMatch(message):
                    _failed = true;
                    yield return Failure(line.Str("message") ?? "Codex: ошибка.", line.Prop("error"));
                    break;
                case "error":
                    // A reconnect after a terminal event starts an unfinished attempt.
                    _turnCompleted = false;
                    break;
            }
        }

        public IEnumerable<NativeChatEvent> Complete()
        {
            if (!_turnCompleted && !_failed)
                yield return Failure("Codex: поток завершился без подтверждения успешного хода.");
        }
    }
}
