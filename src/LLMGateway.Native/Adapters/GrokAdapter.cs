using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native.Adapters;

/// <summary>
/// xAI Grok Build CLI. Chat: <c>--prompt-file … --output-format streaming-json</c>. Usage: the CLI's own
/// ACP agent (<c>grok agent stdio</c>) extension method <c>x.ai/billing</c> — the same data as its <c>/usage</c> screen.
/// </summary>
public sealed class GrokAdapter : NativeAdapterBase
{
    private readonly string _promptDirectory;
    private readonly Func<string, string, CancellationToken, Task> _writePrompt;
    private readonly ConcurrentDictionary<string, PendingPromptCleanup> _pendingPromptCleanup = new();

    public GrokAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<GrokAdapter> logger)
        : this(resolver, options, logger, Path.Combine(Path.GetTempPath(), "LLMGateway", "prompts"),
            static (path, prompt, token) => File.WriteAllTextAsync(path, prompt, new UTF8Encoding(false), token))
    {
    }

    internal GrokAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<GrokAdapter> logger,
        string ownedPromptDirectory, Func<string, string, CancellationToken, Task> writePrompt)
        : base(resolver, options, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedPromptDirectory);
        ArgumentNullException.ThrowIfNull(writePrompt);
        _promptDirectory = ownedPromptDirectory;
        _writePrompt = writePrompt;
    }

    public override ProviderKind Provider => ProviderKind.Grok;
    public override string DisplayName => "Grok";
    public override string DefaultExecutable => "grok";

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsQuota: true,
        QuotaSource: "grok agent stdio (ACP) · billing extension",
        MultiAccount: MultiAccountSupport.Single,
        MultiAccountNotes: "Grok CLI хранит вход в ~/.grok одного пользователя ОС. Смена аккаунта — штатные grok logout / grok login.",
        ConfigDirectoryVariable: null,
        ApiKeyTargetVariable: null,
        SupportsReasoningEffort: true,
        MaxPromptCharacters: 1_000_000);

    protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => ["login"];

    public override async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["--no-auto-update", "models"], cancellationToken).ConfigureAwait(false);
        if (!result.Success) return StatusFromFailure(result);
        var loggedIn = Lines(result.StandardOutput).FirstOrDefault(l => l.StartsWith("You are logged in", StringComparison.OrdinalIgnoreCase));
        if (loggedIn is null)
            return new AccountStatus(AccountAvailability.AuthenticationRequired, null, null, "Grok CLI не сообщил о входе. Нажмите «Войти».", DateTimeOffset.UtcNow);
        var via = loggedIn.Contains(" with ", StringComparison.OrdinalIgnoreCase) ? loggedIn[(loggedIn.IndexOf(" with ", StringComparison.OrdinalIgnoreCase) + 6)..].TrimEnd('.') : null;
        return new AccountStatus(AccountAvailability.Ready, new AccountIdentity(null, via, null), null, null, DateTimeOffset.UtcNow);
    }

    public override async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["--no-auto-update", "models"], cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseModels(result.StandardOutput) : [];
    }

    public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        await using var rpc = JsonRpcStdioClient.Start(Launch(account, ["--no-auto-update", "agent", "--no-leader", "stdio"]));
        await rpc.RequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
            clientInfo = new { name = "llmgateway", version = "2.0" }
        }, ShortTimeout, cancellationToken).ConfigureAwait(false);
        // The CLI's own /usage panel reads this extension. Names differ by build; neither path reads auth files.
        GatewayException? missing = null;
        foreach (var method in new[] { "_x.ai/billing", "x.ai/billing" })
        {
            try
            {
                var billing = await rpc.RequestAsync(method, null, ShortTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = ParseBilling(account, billing);
                return snapshot with { Source = "grok agent stdio (ACP) · " + method };
            }
            catch (GatewayException ex) when (RpcErrors.IsMissingMethod(ex))
            {
                missing = ex;
            }
        }
        return QuotaSnapshot.Unsupported(account, AccountAvailability.Unknown, null, Capabilities.QuotaSource,
            "Эта сборка Grok не отдала расширение billing агенту. " + (missing?.Message ?? "Квоты недоступны без интерактивного /usage."));
    }

    internal static QuotaSnapshot ParseBilling(AccountProfile account, JsonElement billing)
    {
        var config = billing.Prop("config");
        var tier = billing.Str("subscription_tier") ?? billing.Str("subscriptionTier");
        var buckets = new List<QuotaBucket>();
        var period = config.Prop("currentPeriod");
        var start = period.Date("start") ?? config.Date("billingPeriodStart");
        var end = period.Date("end") ?? config.Date("billingPeriodEnd") ?? config.Prop("billingCycle").Date("billingPeriodEnd");
        var used = config.Num("creditUsagePercent");
        if (used is null && config.Prop("monthlyLimit").Num("val") is > 0 and var limit
            && (config.Prop("used").Num("val") ?? config.Prop("usage").Prop("totalUsed").Num("val")) is { } spent)
            used = Math.Clamp(spent / limit * 100, 0, 100);
        if (used is not null || end is not null)
        {
            var label = period.Str("type") switch
            {
                "USAGE_PERIOD_TYPE_WEEKLY" => "Недельный пул",
                "USAGE_PERIOD_TYPE_MONTHLY" => "Месячный пул",
                _ => "Период оплаты"
            };
            int? minutes = start is { } s && end is { } e ? (int)(e - s).TotalMinutes : null;
            buckets.Add(new QuotaBucket("credits", label, used ?? 0, WindowMinutes: minutes, ResetsAt: end, Unit: "%"));
        }
        if (config.Prop("onDemandCap").Num("val") is > 0 and var cap)
            buckets.Add(new QuotaBucket("on_demand", "On-demand", null, Used: (config.Prop("onDemandUsed").Num("val") ?? 0) / 100, Limit: cap / 100, Unit: "USD", ResetsAt: end));
        if (config.Prop("prepaidBalance").Num("val") is > 0 and var prepaid)
            buckets.Add(new QuotaBucket("prepaid", "Предоплаченные кредиты", null, Used: 0, Limit: prepaid / 100, Unit: "USD"));
        var availability = used >= 100 ? AccountAvailability.RateLimited : AccountAvailability.Ready;
        return new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, availability, true, tier, buckets,
            "grok agent stdio (ACP) · x.ai/billing", buckets.Count == 0 ? "Grok не вернул данные об использовании." : null);
    }

    public override async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var directory = EnsureDirectory(_promptDirectory);
        var promptFile = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await _writePrompt(promptFile, request.Prompt, cancellationToken).ConfigureAwait(false);
            var arguments = new List<string>
            {
                "--no-auto-update", "--prompt-file", promptFile, "--output-format", "streaming-json",
                "--permission-mode", "plan", "--no-subagents", "--max-turns", "1", "--disable-web-search"
            };
            if (ModelArgument(request.Model) is { } model) arguments.AddRange(["--model", model]);
            if (Effort(request.ReasoningEffort) is { } effort) arguments.AddRange(["--reasoning-effort", effort]);
            arguments.AddRange(account.ExtraArguments);
            await foreach (var item in StreamAsync(Launch(account, arguments, request.WorkingDirectory), new StreamingJsonParser(), cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            await DeletePromptFileAsync(promptFile).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<NativeModel> ParseModels(string output)
    {
        var models = new List<NativeModel>();
        foreach (var line in Lines(output))
        {
            if (!(line.StartsWith('*') || line.StartsWith('-'))) continue;
            var value = line.TrimStart('*', '-').Trim();
            var isDefault = value.Contains("(default)", StringComparison.OrdinalIgnoreCase);
            var id = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (id is null || !ModelRouter.IsValidModelName(id)) continue;
            models.Add(new NativeModel(id, id, isDefault));
        }
        return models;
    }

    private async Task DeletePromptFileAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    try { await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(false); }
                    catch (Exception wipe) when (wipe is IOException or UnauthorizedAccessException) { }
                    var owner = new PendingPromptCleanup();
                    if (_pendingPromptCleanup.TryAdd(path, owner))
                    {
                        Logger.LogWarning("Temporary prompt cleanup is pending; an owned deletion retry is retained.");
                        owner.Operation = RetryPromptCleanupAsync(path, owner);
                    }
                    return;
                }
                await Task.Delay(40).ConfigureAwait(false);
            }
        }
    }

    private async Task RetryPromptCleanupAsync(string path, PendingPromptCleanup owner)
    {
        // Keep the exact generated path owned after bounded immediate cleanup. This is an
        // in-process retry, not a claim of durable erasure after crash or permanent OS denial.
        while (true)
        {
            try
            {
                File.Delete(path);
                ((ICollection<KeyValuePair<string, PendingPromptCleanup>>)_pendingPromptCleanup).Remove(new(path, owner));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
    }

    private sealed class PendingPromptCleanup
    {
        public Task Operation { get; set; } = Task.CompletedTask;
    }

    internal sealed class StreamingJsonParser : IChatLineParser
    {
        private bool _sawStructuredEvent;
        private bool _sawTerminal;

        public IEnumerable<NativeChatEvent> Complete()
        {
            if (_sawStructuredEvent && !_sawTerminal)
                yield return Failure("Grok завершился без итогового end.");
        }

        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            var eventType = line.Str("type");
            if (eventType is "text" or "thinking" or "reasoning" or "end" or "error") _sawStructuredEvent = true;
            switch (eventType)
            {
                case "text":
                    if (line.Str("data") is { Length: > 0 } text) yield return NativeChatEvent.Delta(text);
                    break;
                case "thinking" or "reasoning":
                    if (line.Str("data") is { Length: > 0 } thought) yield return new NativeChatEvent(NativeChatEventKind.Reasoning, thought);
                    break;
                case "end":
                    _sawTerminal = true;
                    var usage = line.Prop("usage");
                    if (usage is not null)
                    {
                        var cached = usage.Int("cache_read_input_tokens");
                        var input = usage.Int("input_tokens") + cached + usage.Int("cache_creation_input_tokens");
                        var output = usage.Int("output_tokens");
                        yield return NativeChatEvent.Reported(new TokenUsage(input, output, input + output, cached, usage.Int("reasoning_tokens")));
                    }
                    if (line.Str("stopReason") is "refusal" or "error") yield return Failure(line.Str("error") ?? $"Grok: stopReason={line.Str("stopReason")}", line.Prop("error"));
                    break;
                case "error":
                    yield return Failure(line.Str("message") ?? line.Str("data") ?? line.Prop("error").Str("message") ?? "Grok: ошибка.", line.Prop("error"));
                    break;
            }
        }
    }
}
