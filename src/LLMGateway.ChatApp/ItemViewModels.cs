using System.Collections.ObjectModel;
using System.Globalization;
using LLMGateway.Core;

namespace LLMGateway.ChatApp;

public sealed class ChatMessageViewModel : ObservableObject
{
    private string _content;
    private string _header;
    private string _reasoning = string.Empty;
    private string? _footer;
    private bool _isError;

    public ChatMessageViewModel(ChatRole role, string content, string? header = null)
    {
        Role = role;
        _content = content;
        _header = header ?? role switch
        {
            ChatRole.User => "Вы",
            ChatRole.Assistant => "Ассистент",
            ChatRole.Tool => "Результат функции",
            _ => role.ToString()
        };
    }

    public ChatRole Role { get; }

    public string Header
    {
        get => _header;
        set => Set(ref _header, value);
    }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool IncludeInHistory { get; set; } = true;

    public string Content
    {
        get => _content;
        set => Set(ref _content, value);
    }

    public string Reasoning
    {
        get => _reasoning;
        set
        {
            if (Set(ref _reasoning, value)) Raise(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => _reasoning.Length > 0;

    public string? Footer
    {
        get => _footer;
        set => Set(ref _footer, value);
    }

    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value);
    }

    public bool IsUser => Role == ChatRole.User;

    public ChatMessage ToMessage() => new(Role, Content)
    {
        ToolCalls = ToolCalls,
        ToolCallId = ToolCallId,
        Name = ToolName
    };
}

public sealed class QuotaBucketViewModel : ObservableObject
{
    private readonly QuotaBucket _bucket;
    private string _resetText = string.Empty;

    public QuotaBucketViewModel(QuotaBucket bucket)
    {
        _bucket = bucket;
        Tick();
    }

    public string Label => _bucket.Label;
    public double Used => _bucket.UsedPercent ?? (_bucket.Limit is > 0 && _bucket.Used is { } used ? used / _bucket.Limit.Value * 100 : 0);
    public bool HasPercent => _bucket.UsedPercent is not null || _bucket.Limit is > 0;

    public string ValueText => _bucket.UsedPercent is { } percent
        ? $"использовано {percent:0.#}% · осталось {100 - percent:0.#}%"
        : _bucket.Limit is { } limit
            ? $"{_bucket.Used ?? 0:0.##} / {limit:0.##} {_bucket.Unit} · осталось {_bucket.Remaining:0.##}"
            : string.Empty;

    public string ResetText
    {
        get => _resetText;
        private set => Set(ref _resetText, value);
    }

    public void Tick()
    {
        if (_bucket.ResetsAt is not { } reset)
        {
            ResetText = _bucket.WindowMinutes is { } minutes ? $"окно {FormatSpan(TimeSpan.FromMinutes(minutes))}" : string.Empty;
            return;
        }
        var left = reset - DateTimeOffset.UtcNow;
        ResetText = left <= TimeSpan.Zero
            ? $"сброшен {reset.ToLocalTime():dd.MM HH:mm}"
            : $"сброс через {FormatSpan(left)} ({reset.ToLocalTime():dd.MM HH:mm})";
    }

    public static string FormatSpan(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} д {span.Hours} ч"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : $"{Math.Max(0, span.Minutes)} мин {Math.Max(0, span.Seconds)} с";
}

public sealed class QuotaCardViewModel
{
    public QuotaCardViewModel(QuotaSnapshot snapshot, AccountInfo? account)
    {
        Snapshot = snapshot;
        Title = $"{snapshot.Provider} · {account?.DisplayName ?? snapshot.AccountId}";
        Buckets = new ObservableCollection<QuotaBucketViewModel>(snapshot.Buckets.Select(b => new QuotaBucketViewModel(b)));
        var parts = new List<string> { snapshot.Availability.ToString() };
        if (snapshot.Plan is { Length: > 0 } plan) parts.Add("тариф: " + plan);
        if (account?.Identity?.Email is { Length: > 0 } email) parts.Add(email);
        if (snapshot.IsStale) parts.Add("устаревшие данные");
        Status = string.Join(" · ", parts);
        Details = $"{snapshot.Source} · {snapshot.FetchedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
    }

    public QuotaSnapshot Snapshot { get; }
    public string Title { get; }
    public string Status { get; }
    public string? Message => Snapshot.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Snapshot.Message);
    public string Details { get; }
    public ObservableCollection<QuotaBucketViewModel> Buckets { get; }
}

public sealed class AccountEditorViewModel : ObservableObject
{
    private string _id = string.Empty;
    private string _displayName = string.Empty;
    private ProviderKind _provider = ProviderKind.Codex;
    private string _executable = string.Empty;
    private string _configDirectory = string.Empty;
    private AccountAuthMode _authMode;
    private string _apiKeyVariable = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _defaultModel = string.Empty;
    private string _extraArguments = string.Empty;
    private bool _enabled = true;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public ProviderKind Provider { get => _provider; set => Set(ref _provider, value); }
    public string Executable { get => _executable; set => Set(ref _executable, value); }
    public string ConfigDirectory { get => _configDirectory; set => Set(ref _configDirectory, value); }
    public AccountAuthMode AuthMode { get => _authMode; set => Set(ref _authMode, value); }
    public string ApiKeyVariable { get => _apiKeyVariable; set => Set(ref _apiKeyVariable, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public string DefaultModel { get => _defaultModel; set => Set(ref _defaultModel, value); }
    public string ExtraArguments { get => _extraArguments; set => Set(ref _extraArguments, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public void Load(AccountInfo account)
    {
        Id = account.Id;
        DisplayName = account.DisplayName;
        Provider = account.Provider;
        Executable = account.Executable ?? string.Empty;
        ConfigDirectory = account.ConfigDirectory ?? string.Empty;
        AuthMode = account.AuthMode;
        ApiKeyVariable = account.ApiKeyVariable ?? string.Empty;
        WorkingDirectory = account.WorkingDirectory ?? string.Empty;
        DefaultModel = account.DefaultModel ?? string.Empty;
        ExtraArguments = CliArguments.Format(account.ExtraArguments);
        EnvironmentText = string.Join(System.Environment.NewLine, (account.Environment ?? new Dictionary<string, string>()).Select(p => $"{p.Key}={p.Value}"));
        Enabled = account.Enabled;
    }

    private string _environmentText = string.Empty;

    /// <summary>Non-secret variables as one "NAME=value" entry per line. Keys belong in ApiKeyVariable, not here.</summary>
    public string EnvironmentText { get => _environmentText; set => Set(ref _environmentText, value); }

    private Dictionary<string, string> ParseEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in EnvironmentText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(pair)) continue;
            var index = pair.IndexOf('=');
            if (index <= 0 || string.IsNullOrWhiteSpace(pair[..index]))
                throw GatewayException.Invalid("Each environment entry requires a name and '='.");
            if (!result.TryAdd(pair[..index].Trim(), pair[(index + 1)..]))
                throw GatewayException.Invalid("Environment variable names must be unique.");
        }
        return result;
    }

    public AccountProfile ToProfile() => new()
    {
        Id = Id.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id.Trim() : DisplayName.Trim(),
        Provider = Provider,
        Executable = Blank(Executable),
        ConfigDirectory = Blank(ConfigDirectory),
        AuthMode = AuthMode,
        ApiKeyVariable = Blank(ApiKeyVariable),
        WorkingDirectory = Blank(WorkingDirectory),
        DefaultModel = Blank(DefaultModel),
        ExtraArguments = CliArguments.Parse(ExtraArguments).ToList(),
        Environment = ParseEnvironment(),
        Enabled = Enabled
    };

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
