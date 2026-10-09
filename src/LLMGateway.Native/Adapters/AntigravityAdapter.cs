using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native.Adapters;

/// <summary>
/// Google Antigravity CLI (<c>agy</c>). Chat: <c>--print --output-format stream-json --mode plan</c>.
/// The CLI keeps one Google sign-in per machine (OS keyring) and has no profile directory override, so
/// extra accounts are either Gemini API key profiles (documented <c>GEMINI_API_KEY</c> mode) or a native re-login.
/// </summary>
public sealed class AntigravityAdapter : NativeAdapterBase
{
    private static readonly ConcurrentDictionary<string, object> SettingsGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public AntigravityAdapter(ExecutableResolver resolver, GatewayOptions options, ILogger<AntigravityAdapter> logger) : base(resolver, options, logger)
    {
    }

    public override ProviderKind Provider => ProviderKind.Antigravity;
    public override string DisplayName => "Antigravity";
    public override string DefaultExecutable => "agy";

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsQuota: false,
        QuotaSource: "agy не публикует квоты; лимит фиксируется по ответам клиента",
        MultiAccount: MultiAccountSupport.SingleLoginWithApiKeys,
        MultiAccountNotes: "agy хранит один Google-вход на машину (системное хранилище ключей) и не даёт второго слота. " +
            "Дополнительные аккаунты работают одновременно как профили «API-ключ из окружения»: у каждого свой домашний каталог " +
            "с modelProvider=gemini, ключ берётся из указанной переменной и передаётся как GEMINI_API_KEY. Общий ~/.gemini не изменяется. " +
            "Сменить единственный Google-аккаунт — «Войти» и штатный выход/вход в agy.",
        ConfigDirectoryVariable: null,
        ApiKeyTargetVariable: "GEMINI_API_KEY",
        SupportsReasoningEffort: true,
        MaxPromptCharacters: 1_000_000);

    /// <summary>
    /// Headless agy denies tools that need approval and then returns no answer at all; the gateway never
    /// passes --dangerously-skip-permissions, so the model is asked to answer in text instead.
    /// </summary>
    internal const string ChatOnlyPreamble =
        "[Answer directly in plain text. Do not run commands, read or edit files, or use any tools.]\n\n";

    protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => [];

    public override async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["models"], cancellationToken).ConfigureAwait(false);
        if (!result.Success) return StatusFromFailure(result);
        var models = ParseModels(result.StandardOutput);
        if (models.Count == 0) return StatusFromFailure(result with { ExitCode = 1 });
        var mode = account.AuthMode == AccountAuthMode.ApiKeyFromEnvironment ? $"API-ключ ({account.ApiKeyVariable})" : "Google-вход";
        return new AccountStatus(AccountAvailability.Ready, new AccountIdentity(null, mode, null), null, null, DateTimeOffset.UtcNow);
    }

    public override async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(account, ["models"], cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseModels(result.StandardOutput) : [];
    }

    public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(account, cancellationToken).ConfigureAwait(false);
        return QuotaSnapshot.Unsupported(account, status.Availability, status.Identity?.Plan, "agy",
            status.Message ?? "Antigravity CLI не публикует квоты и время сброса. Исчерпание лимита фиксируется по ответам клиента.");
    }

    public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(30, (int)request.Timeout.TotalSeconds);
        // Documented stdin channel: --input-format stream-json. --print would put the prompt on argv.
        var arguments = new List<string> { "--mode", "plan", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", $"{seconds}s" };
        if (ModelArgument(request.Model) is { } model) arguments.AddRange(["--model", model]);
        if (Effort(request.ReasoningEffort) is { } effort && effort is "low" or "medium" or "high") arguments.AddRange(["--effort", effort]);
        arguments.AddRange(account.ExtraArguments);
        var line = JsonSerializer.Serialize(new { @event = "user", message = new { content = ChatOnlyPreamble + request.Prompt } });
        return StreamAsync(Launch(account, arguments, request.WorkingDirectory, line + "\n"), new StreamJsonParser(), cancellationToken);
    }

    protected override IReadOnlyDictionary<string, string?> BuildEnvironment(AccountProfile account, bool forLogin = false)
    {
        var environment = new Dictionary<string, string?>(base.BuildEnvironment(account, forLogin), StringComparer.OrdinalIgnoreCase);
        if (forLogin || account.AuthMode != AccountAuthMode.ApiKeyFromEnvironment) return environment;
        var home = ResolveApiKeyHome(account);
        EnsureApiKeySettings(home);
        environment["USERPROFILE"] = home;
        environment["HOME"] = home;
        return environment;
    }

    /// <summary>
    /// Home directory for one Gemini API key profile. The CLI reads <c>~/.gemini</c> from this home,
    /// so several keys can run at once without editing the real user settings or the OS keyring.
    /// </summary>
    internal static string ResolveApiKeyHome(AccountProfile account)
    {
        var home = string.IsNullOrWhiteSpace(account.ConfigDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMGateway", "antigravity", account.Id)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(account.ConfigDirectory));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (user.Length > 0 && SamePath(home, user))
            throw GatewayException.Invalid("Каталог профиля Antigravity не может совпадать с домашним каталогом: общий ~/.gemini шлюз не изменяет.");
        return home;
    }

    internal static void EnsureApiKeySettings(string home)
    {
        var directory = Path.Combine(Path.GetFullPath(home), ".gemini", "antigravity-cli");
        var file = Path.Combine(directory, "settings.json");
        lock (SettingsGates.GetOrAdd(file, static _ => new object()))
        {
            Directory.CreateDirectory(directory);
            JsonObject settings;
            if (File.Exists(file))
            {
                try
                {
                    settings = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                        ?? throw GatewayException.Invalid("Настройки профиля Antigravity должны быть JSON-объектом; исходный файл сохранён.");
                }
                catch (JsonException)
                {
                    throw GatewayException.Invalid("Настройки профиля Antigravity повреждены; исходный файл сохранён.");
                }
            }
            else settings = new JsonObject();
            if (settings["modelProvider"] is JsonValue current && current.TryGetValue<string>(out var provider) && provider == "gemini")
                return;
            settings["modelProvider"] = "gemini";
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var payload = new UTF8Encoding(false).GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(payload);
                    output.Flush(flushToDisk: true);
                }
                // Same-directory publication preserves existing reader snapshots. Never truncate
                // the authoritative settings file or modify any unrelated JSON fields.
                if (File.Exists(file)) File.Replace(temporary, file, destinationBackupFileName: null);
                else File.Move(temporary, file);
            }
            finally { File.Delete(temporary); }
        }
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<NativeModel> ParseModels(string output)
    {
        var models = new List<NativeModel>();
        foreach (var line in Lines(output))
        {
            var tab = line.IndexOf('\t');
            var id = (tab > 0 ? line[..tab] : line).Trim();
            if (id.Contains(' ') || !id.Contains('-') || !ModelRouter.IsValidModelName(id)) continue;
            models.Add(new NativeModel(id, tab > 0 ? line[(tab + 1)..].Trim() : id, models.Count == 0));
        }
        return models;
    }

    internal sealed class StreamJsonParser : IChatLineParser
    {
        private bool _sawStructuredEvent;
        private bool _sawTerminal;

        public IEnumerable<NativeChatEvent> Complete()
        {
            if (_sawStructuredEvent && !_sawTerminal)
                yield return Failure("Antigravity завершился без итогового result.");
        }

        public IEnumerable<NativeChatEvent> Parse(JsonElement line)
        {
            var eventType = line.Str("event");
            if (eventType is "step_update" or "result" or "error") _sawStructuredEvent = true;
            switch (eventType)
            {
                case "step_update":
                    var step = line.Prop("step_update");
                    var delta = step.Str("text_delta");
                    if (string.IsNullOrEmpty(delta)) yield break;
                    var type = step.Str("step_type");
                    if (type == "agent_response") yield return NativeChatEvent.Delta(delta);
                    else if (type is "thinking" or "agent_thinking" or "reasoning") yield return new NativeChatEvent(NativeChatEventKind.Reasoning, delta);
                    break;
                case "result":
                    _sawTerminal = true;
                    var result = line.Prop("result");
                    var status = result.Str("status");
                    if (status is not null && !status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return Failure(result.Str("error") ?? result.Str("message") ?? result.Str("response") ?? $"Antigravity: статус {status}.", result.Prop("error"));
                        yield break;
                    }
                    var usage = result.Prop("usage");
                    if (usage is not null)
                    {
                        var input = usage.Int("input_tokens");
                        var output = usage.Int("output_tokens");
                        yield return NativeChatEvent.Reported(new TokenUsage(input, output, usage.Int("total_tokens") is > 0 and var total ? total : input + output,
                            usage.Int("cache_read_tokens"), usage.Int("thinking_tokens")));
                    }
                    if (result.Str("response") is { Length: > 0 } response) yield return NativeChatEvent.Final(response);
                    break;
                case "error":
                    yield return Failure(line.Str("message") ?? line.Prop("error").Str("message") ?? "Antigravity: ошибка.");
                    break;
            }
        }
    }
}
