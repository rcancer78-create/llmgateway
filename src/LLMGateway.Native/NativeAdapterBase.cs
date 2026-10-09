using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Native;

/// <summary>Per-run parser of a client's NDJSON output.</summary>
public interface IChatLineParser
{
    IEnumerable<NativeChatEvent> Parse(JsonElement line);

    /// <summary>Validate a successful process exit after all output has been parsed.</summary>
    IEnumerable<NativeChatEvent> Complete() => [];
}

public abstract class NativeAdapterBase : IProviderAdapter
{
    private const int MaxPlainOutput = 4 * 1024 * 1024;

    protected static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(45);

    protected NativeAdapterBase(ExecutableResolver resolver, GatewayOptions options, ILogger logger)
    {
        Resolver = resolver;
        Options = options;
        Logger = logger;
    }

    protected ExecutableResolver Resolver { get; }
    protected GatewayOptions Options { get; }
    protected ILogger Logger { get; }

    public abstract ProviderKind Provider { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultExecutable { get; }
    public abstract ProviderCapabilities Capabilities { get; }

    public string? ResolveExecutable(AccountProfile account)
    {
        var target = Resolver.Resolve(ExecutableOf(account));
        return target is { Kind: LaunchKind.Direct } ? target.ResolvedFrom : null;
    }

    public abstract Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken);
    public abstract Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken);
    public abstract IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken);

    public virtual IEnumerable<AccountProfile> DiscoverProfiles() => [];

    /// <summary>Arguments that open the client's own login flow; null when the client has none.</summary>
    protected abstract IReadOnlyList<string>? LoginArguments(AccountProfile account);

    public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        if (account.AuthMode == AccountAuthMode.ApiKeyFromEnvironment)
            throw new GatewayException(GatewayErrorKind.Unsupported, $"Профиль '{account.Id}' использует ключ из переменной {account.ApiKeyVariable}; вход не нужен.");
        var arguments = LoginArguments(account)
            ?? throw new GatewayException(GatewayErrorKind.Unsupported, $"{DisplayName}: штатная команда входа не известна.");
        InteractiveConsole.Open($"LLMGateway - {DisplayName} ({account.Id})", Target(account), arguments, BuildEnvironment(account, forLogin: true), Workspace(account));
        return Task.CompletedTask;
    }

    protected string ExecutableOf(AccountProfile account) =>
        string.IsNullOrWhiteSpace(account.Executable) ? DefaultExecutable : account.Executable!;

    protected LaunchTarget Target(AccountProfile account)
    {
        var name = ExecutableOf(account);
        var target = Resolver.Resolve(name);
        if (target is null)
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, $"{DisplayName}: '{name}' не установлен или его лаунчер не поддерживается.");
        if (target.Kind != LaunchKind.Direct)
            throw new GatewayException(GatewayErrorKind.Unsupported, $"{DisplayName}: '{name}' — неподдерживаемый лаунчер. Укажите исполняемый файл клиента.");
        return target;
    }

    protected string Workspace(AccountProfile account) =>
        EnsureDirectory(string.IsNullOrWhiteSpace(account.WorkingDirectory) ? Options.ResolveWorkspace() : account.WorkingDirectory!);

    protected virtual IReadOnlyDictionary<string, string?> BuildEnvironment(AccountProfile account, bool forLogin = false)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in account.Environment)
        {
            if (AccountEnvironment.IsSecret(key, value))
                throw GatewayException.Invalid($"Переменная окружения '{key}' похожа на секрет и не передаётся процессу клиента.");
            environment[key] = value;
        }
        var credentialNames = (Options.CredentialVariableNames?.Invoke() ?? [])
            .Append(account.ApiKeyVariable).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray();
        AccountEnvironment.RemoveForeignCredentials(environment, null, credentialNames);
        if (Capabilities.ConfigDirectoryVariable is { } configVariable && !string.IsNullOrWhiteSpace(account.ConfigDirectory))
            environment[configVariable] = EnsureDirectory(Path.GetFullPath(ExpandConfigDirectory(account.ConfigDirectory!, environment)));
        string? keep = null;
        if (account.AuthMode == AccountAuthMode.ApiKeyFromEnvironment && !forLogin)
        {
            var target = Capabilities.ApiKeyTargetVariable
                ?? throw new GatewayException(GatewayErrorKind.Unsupported, $"{DisplayName} не поддерживает вход по API-ключу из окружения.");
            var key = Environment.GetEnvironmentVariable(account.ApiKeyVariable ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key))
                throw new GatewayException(GatewayErrorKind.AuthenticationRequired, $"Переменная окружения '{account.ApiKeyVariable}' с ключом для профиля '{account.Id}' не задана.");
            environment[target] = key;
            keep = target;
        }
        AccountEnvironment.RemoveForeignCredentials(environment, keep, credentialNames);
        return environment;
    }

    private static string ExpandConfigDirectory(string path, IReadOnlyDictionary<string, string?> childEnvironment) =>
        System.Text.RegularExpressions.Regex.Replace(path, "%([^%]+)%", match =>
        {
            var name = match.Groups[1].Value;
            if (childEnvironment.TryGetValue(name, out var childValue) && childValue is null
                || AccountEnvironment.IsSecret(name, null))
                throw GatewayException.Invalid("Каталог конфигурации не может ссылаться на переменную с учётными данными.");
            var value = Environment.GetEnvironmentVariable(name);
            if (AccountEnvironment.IsSecret(name, value))
                throw GatewayException.Invalid("Каталог конфигурации не может содержать учётные данные из окружения.");
            return value ?? match.Value;
        });

    protected NativeLaunch Launch(AccountProfile account, IEnumerable<string> arguments, string? workingDirectory = null, string? standardInput = null)
    {
        CliArguments.RejectUnsafe(account.ExtraArguments);
        return new(Target(account), arguments.ToArray(), BuildEnvironment(account), workingDirectory ?? Workspace(account), standardInput);
    }

    protected Task<NativeRunResult> RunAsync(AccountProfile account, IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        NativeProcess.RunAsync(Launch(account, arguments), timeout ?? ShortTimeout, cancellationToken);

    /// <summary>Starts a chat process and converts its NDJSON stdout into events while it is still running.</summary>
    protected async IAsyncEnumerable<NativeChatEvent> StreamAsync(
        NativeLaunch launch,
        IChatLineParser parser,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var process = NativeProcess.Start(launch);
        var plainOutput = new StringBuilder();
        var producedText = false;
        var failed = false;
        var structured = false;
        await foreach (var line in process.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
        {
            var events = ParseLine(parser, line, plainOutput, ref structured);
            foreach (var item in events)
            {
                producedText |= item.Kind is NativeChatEventKind.Text or NativeChatEventKind.FinalText;
                failed |= item.Kind == NativeChatEventKind.Error;
                yield return item;
                if (item.Kind == NativeChatEventKind.Error) yield break;
            }
        }
        var exitCode = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (failed) yield break;
        if (exitCode == 0)
        {
            foreach (var item in parser.Complete())
            {
                producedText |= item.Kind is NativeChatEventKind.Text or NativeChatEventKind.FinalText;
                yield return item;
                if (item.Kind == NativeChatEventKind.Error) yield break;
            }
        }
        // Partial output does not make a nonzero process exit successful.
        if (exitCode == 0 && producedText) yield break;
        // With a structured output format, stray plain lines are client diagnostics rather than the answer.
        if (exitCode != 0 || structured || plainOutput.Length == 0)
        {
            var message = FirstNonEmpty(process.StandardError, plainOutput.ToString(),
                exitCode != 0 ? $"{DisplayName} завершился с кодом {exitCode}." : $"{DisplayName} завершился без ответа.");
            yield return Failure(message);
        }
        else
        {
            yield return NativeChatEvent.Final(plainOutput.ToString().Trim());
        }
    }

    private IReadOnlyList<NativeChatEvent> ParseLine(IChatLineParser parser, string line, StringBuilder plainOutput, ref bool structured)
    {
        // Only a JSON object at the start of the line is a protocol record.
        // A plain answer may contain a JSON example after explanatory text.
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        if (start == line.Length || line[start] != '{')
        {
            return AppendPlainLine(plainOutput, line);
        }
        try
        {
            using var document = JsonDocument.Parse(line.AsMemory(start));
            structured = true;
            return parser.Parse(document.RootElement).ToList();
        }
        catch (JsonException)
        {
            return AppendPlainLine(plainOutput, line);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Logger.LogDebug("{Provider}: unexpected event shape ({ExceptionType})", Provider, ex.GetType().Name);
            return [Failure($"{DisplayName}: unexpected structured event shape.")];
        }
    }

    private IReadOnlyList<NativeChatEvent> AppendPlainLine(StringBuilder output, string line)
    {
        // Plain clients have no deltas: retain the entire answer within the existing native
        // command-output bound, and refuse overflow rather than publish a truncated success.
        if (line.Length > MaxPlainOutput - output.Length - Environment.NewLine.Length)
            return [Failure($"{DisplayName}: native plain output exceeded the command-output bound.")];
        output.AppendLine(line);
        return [];
    }

    protected static NativeChatEvent Failure(string? message, JsonElement? structured = null)
    {
        var text = NativeErrorClassifier.Trim(message);
        var kind = structured is { ValueKind: JsonValueKind.Object } error
            ? NativeErrorClassifier.Classify(error, text)
            : NativeErrorClassifier.Classify(text);
        return NativeChatEvent.Fail(kind, text, NativeErrorClassifier.ParseRetryAt(text, DateTimeOffset.UtcNow));
    }

    protected AccountStatus StatusFromFailure(NativeRunResult result, string? version = null)
    {
        if (result.TimedOut) return new AccountStatus(AccountAvailability.Error, null, version, $"{DisplayName} не ответил вовремя.", DateTimeOffset.UtcNow);
        var message = NativeErrorClassifier.Trim(FirstNonEmpty(result.StandardError, result.StandardOutput, $"Код выхода {result.ExitCode}"), 400);
        var availability = NativeErrorClassifier.Classify(message) switch
        {
            GatewayErrorKind.AuthenticationRequired => AccountAvailability.AuthenticationRequired,
            GatewayErrorKind.RateLimited => AccountAvailability.RateLimited,
            _ => AccountAvailability.Error
        };
        return new AccountStatus(availability, null, version, message, DateTimeOffset.UtcNow);
    }

    protected static string? ModelArgument(string? model) =>
        model is null || model.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : model;

    protected static string? Effort(string? effort) =>
        effort is { Length: >= 2 and <= 12 } && effort.All(char.IsAsciiLetterLower) ? effort : null;

    protected static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    protected static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    protected static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Finds extra isolated config directories next to the default one, e.g. ~/.codex_work.</summary>
    protected IEnumerable<AccountProfile> DiscoverHomeDirectories(string prefix, string defaultDirectory, string[] markers, string idPrefix)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(home, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFullPath(directory).Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase)) continue;
            if (!markers.Any(marker => File.Exists(Path.Combine(directory, marker)))) continue;
            var name = Path.GetFileName(directory);
            var suffix = new string(name.TrimStart('.').Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '-').ToArray());
            yield return new AccountProfile
            {
                Id = idPrefix + suffix,
                DisplayName = $"{DisplayName} · {name}",
                Provider = Provider,
                ConfigDirectory = directory
            };
        }
    }
}

internal static class InteractiveConsole
{
    /// <summary>Opens the native client in a new console window so the user completes its own login flow.</summary>
    public static void Open(string title, LaunchTarget target, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new GatewayException(GatewayErrorKind.Unsupported, "Интерактивный вход поддерживается только в Windows.");
        if (target.Kind != LaunchKind.Direct)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Неподдерживаемый лаунчер. Укажите исполняемый файл клиента.");
        var parts = new List<string> { Quote(target.FileName) };
        parts.AddRange(target.PrefixArguments.Concat(arguments).Select(Quote));
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c start {Quote(title)} /D {Quote(workingDirectory)} {string.Join(' ', parts)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var (key, value) in environment)
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }
        using var process = Process.Start(info)
            ?? throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Не удалось открыть окно входа.");
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!', '\r', '\n']) >= 0)
            throw new GatewayException(GatewayErrorKind.Unsupported, $"Путь или аргумент '{value}' содержит символы, недопустимые для запуска окна входа.");
        return '"' + value + '"';
    }
}
