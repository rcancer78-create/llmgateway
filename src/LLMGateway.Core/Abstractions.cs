namespace LLMGateway.Core;

/// <summary>
/// Single entry point used by applications (LLMWorkGUI, the chat app, the HTTP server).
/// Implemented in-process by <see cref="LlmGateway"/> and remotely by <see cref="Client.OpenAiGatewayClient"/>.
/// </summary>
public interface ILlmGateway
{
    /// <summary>Set when <c>accounts.json</c> is damaged and the gateway will not overwrite it.</summary>
    string? AccountStoreWarning { get; }

    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Makes the account the active one for its provider.</summary>
    Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Asks the native client about installation and authentication state.</summary>
    Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Opens the native client's own interactive login for the account (on the gateway machine).</summary>
    Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default);
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

public interface IAccountStore
{
    long RevisionId { get; }

    /// <summary>True when the file on disk must not be replaced, for example because it failed to parse.</summary>
    bool IsReadOnly { get; }

    /// <summary>Why <see cref="IsReadOnly"/> is set, suitable for the accounts API and the chat UI.</summary>
    string? PersistenceWarning { get; }

    IReadOnlyList<AccountProfile> GetAll();
    AccountProfile? Find(string accountId);
    Task AddAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task UpdateAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);
    Task SelectAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed class NativeChatRequest
{
    public required string Prompt { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
}

public enum NativeChatEventKind
{
    Text,
    Reasoning,
    Usage,

    /// <summary>Full answer reported at the end; used only when no <see cref="Text"/> deltas were produced.</summary>
    FinalText,
    Error
}

public sealed record NativeChatEvent(
    NativeChatEventKind Kind,
    string? Text = null,
    TokenUsage? Usage = null,
    GatewayErrorKind ErrorKind = GatewayErrorKind.Upstream,
    DateTimeOffset? RetryAt = null)
{
    public static NativeChatEvent Delta(string text) => new(NativeChatEventKind.Text, text);
    public static NativeChatEvent Final(string text) => new(NativeChatEventKind.FinalText, text);
    public static NativeChatEvent Reported(TokenUsage usage) => new(NativeChatEventKind.Usage, Usage: usage);
    public static NativeChatEvent Fail(GatewayErrorKind kind, string message, DateTimeOffset? retryAt = null) => new(NativeChatEventKind.Error, message, ErrorKind: kind, RetryAt: retryAt);
}

/// <summary>
/// Adapter for one native client. Implementations must only use the client's public CLI/RPC surface,
/// must not read credential files and must not call private provider endpoints themselves.
/// </summary>
public interface IProviderAdapter
{
    ProviderKind Provider { get; }
    string DisplayName { get; }
    string DefaultExecutable { get; }
    ProviderCapabilities Capabilities { get; }

    /// <summary>Resolved executable path or null when the client is not installed.</summary>
    string? ResolveExecutable(AccountProfile account);

    Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken);
    IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken);
    Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken);

    /// <summary>Directories in the user profile that look like extra isolated accounts (e.g. ~/.codex_work).</summary>
    IEnumerable<AccountProfile> DiscoverProfiles();
}

/// <summary>What to do with temperature, top_p, penalties and seed. Native CLIs cannot apply them.</summary>
public enum UnsupportedSamplingParameterPolicy
{
    /// <summary>Accept the request and write a journal note. Cline, Roo and Continue send these by default.</summary>
    Ignore,

    /// <summary>Reject the request with HTTP 400.</summary>
    Reject
}

public sealed class GatewayOptions
{
    /// <summary>accounts.json location. Default: %LOCALAPPDATA%/LLMGateway/accounts.json.</summary>
    public string? AccountsFile { get; init; }

    /// <summary>Working directory for native agents. Default: an empty %LOCALAPPDATA%/LLMGateway/workspace.</summary>
    public string? WorkspaceDirectory { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 600;
    public int QuotaCacheSeconds { get; init; } = 60;
    public int ModelCacheSeconds { get; init; } = 600;

    /// <summary>How long a chat waits for a free account slot before HTTP 503 and Retry-After.</summary>
    public int SlotWaitSeconds { get; init; } = 60;

    /// <summary>Minimum gap between catalog refreshes caused by an unknown model name.</summary>
    public int CatalogRefreshCooldownSeconds { get; init; } = 30;

    public int MaxConcurrentRequestsPerAccount { get; init; } = 2;
    public ProviderKind DefaultProvider { get; init; } = ProviderKind.Codex;
    public bool DiscoverProfiles { get; init; } = true;
    public UnsupportedSamplingParameterPolicy UnsupportedSamplingParameters { get; init; } = UnsupportedSamplingParameterPolicy.Ignore;

    /// <summary>
    /// Host variable names that hold API keys for configured profiles.
    /// Child processes drop every name except the variable the running profile sets explicitly.
    /// </summary>
    public Func<IEnumerable<string>>? CredentialVariableNames { get; set; }

    public string ResolveAccountsFile() => string.IsNullOrWhiteSpace(AccountsFile)
        ? Path.Combine(DataDirectory, "accounts.json")
        : Path.GetFullPath(AccountsFile);

    public string ResolveWorkspace() => string.IsNullOrWhiteSpace(WorkspaceDirectory)
        ? Path.Combine(DataDirectory, "workspace")
        : Path.GetFullPath(WorkspaceDirectory);

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMGateway");
}
