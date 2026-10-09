namespace LLMGateway.Core;

public enum ProviderKind
{
    Codex,
    Cursor,
    Antigravity,
    Grok,
    Claude,

    /// <summary>A remote OpenAI-compatible server did not identify the provider. Not a stored account.</summary>
    Unknown
}

public enum AccountAvailability
{
    Unknown,
    Ready,
    NotInstalled,
    AuthenticationRequired,
    RateLimited,
    Disabled,
    Error
}

/// <summary>How the native client authenticates this profile. The gateway never stores secrets.</summary>
public enum AccountAuthMode
{
    /// <summary>Uses the login performed inside the native client (OAuth/subscription).</summary>
    NativeLogin,

    /// <summary>The native client reads an API key from its environment; the key is taken from a host variable named in <see cref="AccountProfile.ApiKeyVariable"/>.</summary>
    ApiKeyFromEnvironment
}

/// <summary>Persisted, secret-free description of one native client account/profile.</summary>
public sealed class AccountProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderKind Provider { get; set; }

    /// <summary>Executable name or full path. Empty means the provider default (codex, cursor-agent, agy, grok, claude).</summary>
    public string? Executable { get; set; }

    /// <summary>Isolated native configuration directory (CODEX_HOME for Codex, CLAUDE_CONFIG_DIR for Claude).</summary>
    public string? ConfigDirectory { get; set; }

    public AccountAuthMode AuthMode { get; set; } = AccountAuthMode.NativeLogin;

    /// <summary>Name of a host environment variable holding the API key. Only the name is persisted.</summary>
    public string? ApiKeyVariable { get; set; }

    public string? WorkingDirectory { get; set; }
    public string? DefaultModel { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExtraArguments { get; set; } = [];
    public bool IsActive { get; set; }
    public bool Enabled { get; set; } = true;

    public AccountProfile Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Provider = Provider,
        Executable = Executable,
        ConfigDirectory = ConfigDirectory,
        AuthMode = AuthMode,
        ApiKeyVariable = ApiKeyVariable,
        WorkingDirectory = WorkingDirectory,
        DefaultModel = DefaultModel,
        Environment = CloneEnvironment(),
        ExtraArguments = CloneArguments(),
        IsActive = IsActive,
        Enabled = Enabled
    };
    private Dictionary<string, string> CloneEnvironment()
    {
        if (Environment is null) throw GatewayException.Invalid("Account environment collection is invalid.");
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Environment)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('\0') || pair.Key.Contains('='))
                throw GatewayException.Invalid("Account environment names are invalid.");
            if (pair.Value is null) throw GatewayException.Invalid("Account environment values must not be null.");
            if (pair.Value.Contains('\0')) throw GatewayException.Invalid("Account environment values must not contain NUL.");
            if (!copy.TryAdd(pair.Key, pair.Value))
                throw GatewayException.Invalid("Account environment names must be unique ignoring case.");
        }
        return copy;
    }

    private List<string> CloneArguments()
    {
        if (ExtraArguments is null || ExtraArguments.Any(value => value is null))
            throw GatewayException.Invalid("Account argument collection must not contain null values.");
        return [.. ExtraArguments];
    }

    public AccountProfile Freeze()
    {
        if (Environment is null || ExtraArguments is null)
            throw GatewayException.Invalid("Профиль содержит некорректные коллекции настроек.");
        return Clone();
    }
}

public sealed record AccountIdentity(string? Email, string? Plan, string? ProviderAccountId);

public sealed record AccountStatus(
    AccountAvailability Availability,
    AccountIdentity? Identity,
    string? ClientVersion,
    string? Message,
    DateTimeOffset CheckedAt);

public sealed record AccountInfo(
    string Id,
    string DisplayName,
    ProviderKind Provider,
    bool IsActive,
    bool Enabled,
    AccountAvailability Availability,
    AccountIdentity? Identity,
    string? Executable,
    string? ConfigDirectory,
    AccountAuthMode AuthMode,
    string? ApiKeyVariable,
    string? WorkingDirectory,
    string? DefaultModel,
    string? Message,
    DateTimeOffset? CheckedAt,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? ExtraArguments = null);

public enum MultiAccountSupport
{
    /// <summary>Several accounts can be used concurrently (separate native config directories).</summary>
    Isolated,

    /// <summary>One native login per machine; extra accounts need API keys or a native re-login.</summary>
    SingleLoginWithApiKeys,

    /// <summary>One account per machine.</summary>
    Single
}

public sealed record ProviderCapabilities(
    bool SupportsQuota,
    string QuotaSource,
    MultiAccountSupport MultiAccount,
    string MultiAccountNotes,
    string? ConfigDirectoryVariable,
    string? ApiKeyTargetVariable,
    bool SupportsReasoningEffort,
    int MaxPromptCharacters)
{
    /// <summary>JSON Schema response validation is unavailable; schema requests fail before native dispatch.</summary>
    public bool SupportsJsonSchema => false;
}

public sealed record ProviderInfo(
    ProviderKind Provider,
    string DisplayName,
    string DefaultExecutable,
    bool Installed,
    string? ResolvedPath,
    ProviderCapabilities Capabilities);
