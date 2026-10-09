using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMGateway.Core.Client;
using LLMGateway.Native;
using LLMGateway.Server;

namespace LLMGateway.ChatApp;

public enum ConnectionMode
{
    Library,
    Api
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ToolDefinition DemoTool = new(
        "get_current_time",
        "Возвращает текущие дату и время на компьютере пользователя.",
        """{"type":"object","properties":{"utc":{"type":"boolean","description":"true — время UTC"}},"required":[]}""");

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<LlmGateway> _libraryFactory;
    private readonly Func<ILlmGateway, string, GatewayServerOptions, CancellationToken, Task<EmbeddedGatewayServer>> _startServer;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _serverStartup;
    private Task? _chatOperation;
    private readonly HashSet<Task> _admittedCommands = [];
    private Task? _connectionCommand;
    private bool _changingConnection;
    private Task? _disposal;
    private bool _stopping;
    private LlmGateway? _library;
    private OpenAiGatewayClient? _remote;
    private ILlmGateway? _gateway;
    private EmbeddedGatewayServer? _server;
    private EmbeddedGatewayServer? _retiringServer;
    private CancellationTokenSource? _chatCancellation;
    private int _quotaCountdown;

    private ConnectionMode _mode = ConnectionMode.Library;
    private string _apiUrl = "http://127.0.0.1:5157";
    private string _apiKey = string.Empty;
    private string _embeddedUrl = "http://127.0.0.1:5158";
    private string _connectionState = "Не подключено";
    private string _status = "Готово";
    private AccountInfo? _selectedAccount;
    private GatewayModel? _selectedModel;
    private string _modelFilterAccount = AllAccounts;
    private string _input = string.Empty;
    private string _systemPrompt = string.Empty;
    private bool _stream = true;
    private string _reasoningEffort = DefaultEffort;
    private bool _useDemoTool;
    private bool _jsonMode;
    private string _stopSequence = string.Empty;
    private bool _autoRefreshQuotas = true;
    private bool _isChatting;

    public const string AllAccounts = "(все аккаунты)";

    public MainViewModel() : this(
        () => NativeGateway.Create(new GatewayOptions()),
        (gateway, url, options, token) => EmbeddedGatewayServer.StartAsync(gateway, url, options, cancellationToken: token))
    {
    }

    internal MainViewModel(Func<LlmGateway> libraryFactory,
        Func<ILlmGateway, string, GatewayServerOptions, CancellationToken, Task<EmbeddedGatewayServer>> startServer)
    {
        _libraryFactory = libraryFactory;
        _startServer = startServer;
        void OnError(Exception ex) => ReportError(ex);
        ConnectCommand = CreateCommand(_ => ConnectAsync(), OnError, retiresConnection: true);
        RefreshCommand = CreateCommand(_ => RefreshAllAsync(refresh: true), OnError, _ => _gateway is not null);
        RefreshQuotasCommand = CreateCommand(_ => RefreshQuotasAsync(true), OnError, _ => _gateway is not null);
        CheckAccountCommand = CreateCommand(_ => CheckSelectedAsync(), OnError, _ => _gateway is not null && SelectedAccount is not null);
        CheckAllCommand = CreateCommand(_ => CheckAllAsync(), OnError, _ => _gateway is not null);
        ActivateCommand = CreateCommand(_ => ActivateSelectedAsync(), OnError, _ => _gateway is not null && SelectedAccount is not null);
        LoginCommand = CreateCommand(_ => LoginSelectedAsync(), OnError, _ => _gateway is not null && SelectedAccount is not null);
        RemoveCommand = CreateCommand(_ => RemoveSelectedAsync(), OnError, _ => _gateway is not null && SelectedAccount is not null);
        EditCommand = CreateCommand(_ => { if (SelectedAccount is not null) Editor.Load(SelectedAccount); return Task.CompletedTask; }, OnError, _ => SelectedAccount is not null);
        AddAccountCommand = CreateCommand(_ => SaveAccountAsync(add: true), OnError, _ => _gateway is not null);
        UpdateAccountCommand = CreateCommand(_ => SaveAccountAsync(add: false), OnError, _ => _gateway is not null);
        SendCommand = CreateCommand(_ => SendAsync(), OnError, _ => _gateway is not null && !IsChatting && !string.IsNullOrWhiteSpace(Input));
        StopCommand = CreateCommand(_ => { _chatCancellation?.Cancel(); return Task.CompletedTask; }, OnError, _ => IsChatting,
            allowDuringConnectionChange: true);
        ClearCommand = CreateCommand(_ => { Messages.Clear(); return Task.CompletedTask; }, OnError, _ => !IsChatting);
        StartServerCommand = CreateCommand(_ => StartServerAsync(), OnError, _ => !_stopping && _server is null);
        StopServerCommand = CreateCommand(_ => StopServerAsync(), OnError, _ => !_stopping && _server is not null);
        _clock.Tick += OnClockTick;
        _clock.Start();
    }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshQuotasCommand { get; }
    public RelayCommand CheckAccountCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand AddAccountCommand { get; }
    public RelayCommand UpdateAccountCommand { get; }
    public RelayCommand SendCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand StartServerCommand { get; }
    public RelayCommand StopServerCommand { get; }

    public ObservableCollection<AccountInfo> Accounts { get; } = [];
    public ObservableCollection<GatewayModel> Models { get; } = [];
    public ObservableCollection<string> ModelFilters { get; } = [AllAccounts];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<QuotaCardViewModel> Quotas { get; } = [];
    public ObservableCollection<ProviderInfo> Providers { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public AccountEditorViewModel Editor { get; } = new();

    public IReadOnlyList<ProviderKind> ProviderKinds { get; } = Enum.GetValues<ProviderKind>().Where(provider => provider != ProviderKind.Unknown).ToArray();
    public IReadOnlyList<AccountAuthMode> AuthModes { get; } = Enum.GetValues<AccountAuthMode>();
    public const string DefaultEffort = "(по умолчанию)";
    public IReadOnlyList<string> ReasoningEfforts { get; } = [DefaultEffort, "low", "medium", "high", "xhigh"];

    private List<GatewayModel> _allModels = [];

    public bool IsLibraryMode
    {
        get => _mode == ConnectionMode.Library;
        set { if (value) SetMode(ConnectionMode.Library); }
    }

    public bool IsApiMode
    {
        get => _mode == ConnectionMode.Api;
        set { if (value) SetMode(ConnectionMode.Api); }
    }

    private long _connectionGeneration;
    public string ApiUrl { get => _apiUrl; set { if (Set(ref _apiUrl, value)) ++_connectionGeneration; } }
    public string ApiKey { get => _apiKey; set { if (Set(ref _apiKey, value)) ++_connectionGeneration; } }
    public string EmbeddedUrl { get => _embeddedUrl; set => Set(ref _embeddedUrl, value); }
    public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ServerState => _retiringServer is not null
        ? "Освобождение встроенного API-сервера не подтверждено"
        : _server is null ? "Встроенный API-сервер остановлен" : $"Встроенный API-сервер: {_server.Url}/v1";

    public AccountInfo? SelectedAccount { get => _selectedAccount; set => Set(ref _selectedAccount, value); }

    public GatewayModel? SelectedModel { get => _selectedModel; set => Set(ref _selectedModel, value); }

    public string ModelFilterAccount
    {
        get => _modelFilterAccount;
        set
        {
            if (value is not null && Set(ref _modelFilterAccount, value)) ApplyModelFilter();
        }
    }

    public string Input { get => _input; set => Set(ref _input, value); }
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }
    public bool Stream { get => _stream; set => Set(ref _stream, value); }
    public string ReasoningEffort { get => _reasoningEffort; set => Set(ref _reasoningEffort, value); }
    public bool UseDemoTool { get => _useDemoTool; set => Set(ref _useDemoTool, value); }
    public bool JsonMode { get => _jsonMode; set => Set(ref _jsonMode, value); }
    public string StopSequence { get => _stopSequence; set => Set(ref _stopSequence, value); }
    public bool AutoRefreshQuotas { get => _autoRefreshQuotas; set => Set(ref _autoRefreshQuotas, value); }
    public bool IsChatting { get => _isChatting; private set => Set(ref _isChatting, value); }

    private void SetMode(ConnectionMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        ++_connectionGeneration;
        _gateway = null;
        ConnectionState = "Режим изменён. Нажмите «Подключить».";
        Raise(nameof(IsLibraryMode));
        Raise(nameof(IsApiMode));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var generation = _connectionGeneration;
        OpenAiGatewayClient? remote = null;
        ILlmGateway gateway;
        string connectionState;
        if (_mode == ConnectionMode.Library)
        {
            gateway = EnsureLibrary();
            connectionState = "Режим библиотеки: прямые вызовы ILlmGateway (LLMGateway.Native)";
        }
        else
        {
            if (!Uri.TryCreate(ApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Неверный адрес API.");
            remote = new OpenAiGatewayClient(uri, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim());
            gateway = remote;
            connectionState = $"Режим OpenAI API: {uri}v1 (OpenAiGatewayClient)";
        }
        // This command is already admitted. Exclude its own completion from the drain;
        // retiring the old client must wait for prior commands that still use it.
        var priorCommands = _admittedCommands.Where(task => !ReferenceEquals(task, _connectionCommand)).ToArray();
        try
        {
            try { await Task.WhenAll(priorCommands); }
            catch { /* Completed command failures are already reported by their own RelayCommand. */ }
            _lifetime.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
        }
        catch
        {
            remote?.Dispose();
            throw;
        }
        if (generation != _connectionGeneration)
        {
            remote?.Dispose();
            return; // The form retired this candidate while prior commands were draining.
        }
        _remote?.Dispose();
        _remote = remote;
        _gateway = gateway;
        // Management extensions may be absent on the replacement OpenAI-compatible server.
        // Its empty/unsupported results must never leave the previous gateway's identities active.
        Accounts.Clear();
        Quotas.Clear();
        Providers.Clear();
        _allModels.Clear();
        Models.Clear();
        SelectedAccount = null;
        SelectedModel = null;
        ModelFilters.Clear();
        ModelFilters.Add(AllAccounts);
        ModelFilterAccount = AllAccounts;
        ConnectionState = connectionState;
        AddLog(ConnectionState);
        await RefreshAllAsync(refresh: false);
    }

    private LlmGateway EnsureLibrary()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        return _library ??= _libraryFactory();
    }

    private async Task RefreshAllAsync(bool refresh)
    {
        var gateway = Require();
        Status = "Обновление аккаунтов, моделей и квот…";
        // Each step is independent: a plain OpenAI-compatible server has /v1/models but no /v1/gateway/* extensions.
        await Step("провайдеры", async () =>
        {
            var providers = await gateway.GetProvidersAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(gateway, _gateway)) return;
            Providers.Clear();
            foreach (var provider in providers) Providers.Add(provider);
        });
        if (!ReferenceEquals(gateway, _gateway)) return;
        await Step("аккаунты", async () =>
        {
            await ReloadAccountsAsync(gateway);
            if (ReferenceEquals(gateway, _gateway)) await CheckAllAsync(gateway);
        });
        if (!ReferenceEquals(gateway, _gateway)) return;
        await Step("модели", () => RefreshModelsAsync(refresh, gateway));
        if (!ReferenceEquals(gateway, _gateway)) return;
        await Step("квоты", () => RefreshQuotasAsync(refresh, gateway));
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(gateway, _gateway)) return;
        SetIdleStatus();
        if (gateway.AccountStoreWarning is { } storeWarning) AddLog(storeWarning);

        async Task Step(string name, Func<Task> action)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try { await action(); }
            catch (Exception ex) when (ex is GatewayException or HttpRequestException or System.Text.Json.JsonException)
            {
                if (_stopping) throw;
                AddLog($"Не удалось получить {name}: {GatewayUiDiagnostics.Describe(ex)}");
            }
            _lifetime.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task ReloadAccountsAsync(ILlmGateway? admittedGateway = null)
    {
        var gateway = admittedGateway ?? Require();
        var selectedId = SelectedAccount?.Id;
        var accounts = await gateway.GetAccountsAsync(_lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(gateway, _gateway)) return;
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();
        // Synchronize in place: rebuilding the collection would drop the ComboBox selection.
        var wanted = new List<string> { AllAccounts };
        wanted.AddRange(Accounts.Select(a => a.Id));
        for (var i = ModelFilters.Count - 1; i >= 0; i--)
            if (!wanted.Contains(ModelFilters[i])) ModelFilters.RemoveAt(i);
        foreach (var id in wanted.Where(id => !ModelFilters.Contains(id))) ModelFilters.Add(id);
        if (!ModelFilters.Contains(_modelFilterAccount)) ModelFilterAccount = AllAccounts;
        ApplyModelFilter();
    }

    private async Task CheckAllAsync(ILlmGateway? admittedGateway = null)
    {
        var gateway = admittedGateway ?? Require();
        Status = "Проверка нативных клиентов…";
        var targets = Accounts.Where(a => a.Enabled && a.Availability != AccountAvailability.NotInstalled).Select(a => a.Id).ToList();
        await Task.WhenAll(targets.Select(async id =>
        {
            try { await gateway.CheckAccountAsync(id, _lifetime.Token); }
            catch (GatewayException ex)
            {
                if (_stopping) throw;
                AddLog($"{id}: {GatewayUiDiagnostics.Describe(ex)}");
            }
        }));
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(gateway, _gateway)) return;
        await ReloadAccountsAsync(gateway);
        SetIdleStatus();
    }

    private async Task RefreshModelsAsync(bool refresh, ILlmGateway? admittedGateway = null)
    {
        var gateway = admittedGateway ?? Require();
        Status = "Получение списка моделей у нативных клиентов…";
        var models = await gateway.GetModelsAsync(refresh, cancellationToken: _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(gateway, _gateway)) return;
        _allModels = [.. models];
        ApplyModelFilter();
        AddLog($"Моделей: {_allModels.Count}");
    }

    private void ApplyModelFilter()
    {
        var selected = SelectedModel?.Id;
        Models.Clear();
        var visible = ModelFilterAccount == AllAccounts
            ? _allModels
            : _allModels.Where(m => m.AccountId == ModelFilterAccount);
        foreach (var model in visible.OrderBy(m => m.Provider).ThenByDescending(m => m.IsDefault).ThenBy(m => m.NativeModel)) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(m => m.Id == selected) ?? Models.FirstOrDefault(m => m.IsDefault) ?? Models.FirstOrDefault();
    }

    private async Task RefreshQuotasAsync(bool refresh, ILlmGateway? admittedGateway = null)
    {
        var gateway = admittedGateway ?? Require();
        Status = "Чтение квот…";
        var snapshots = await gateway.GetQuotasAsync(refresh, cancellationToken: _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(gateway, _gateway)) return;
        var accounts = AccountsById();
        Quotas.Clear();
        foreach (var snapshot in snapshots.Where(s => s.AccountId is not null && s.Availability != AccountAvailability.NotInstalled))
            Quotas.Add(new QuotaCardViewModel(snapshot, accounts.GetValueOrDefault(snapshot.AccountId)));
        _quotaCountdown = 60;
        SetIdleStatus();
    }

    private async Task CheckSelectedAsync()
    {
        var info = await Require().CheckAccountAsync(SelectedAccount!.Id, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        AddLog($"{info.Id}: {info.Availability} {info.Identity?.Email} {info.Identity?.Plan} {info.Message}");
        await ReloadAccountsAsync();
    }

    private async Task ActivateSelectedAsync()
    {
        var info = await Require().SelectAccountAsync(SelectedAccount!.Id, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        AddLog($"Активный аккаунт {info.Provider}: {info.Id}");
        await ReloadAccountsAsync();
    }

    private async Task LoginSelectedAsync()
    {
        var account = SelectedAccount!;
        await Require().StartNativeLoginAsync(account.Id, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        AddLog($"{account.Id}: открыто окно штатного входа {account.Provider}. После входа нажмите «Проверить».");
    }

    private async Task RemoveSelectedAsync()
    {
        var account = SelectedAccount!;
        if (MessageBox.Show($"Удалить профиль '{account.Id}'? Файлы нативного клиента не удаляются.", "LLMGateway", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Require().RemoveAccountAsync(account.Id, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        AddLog($"Профиль {account.Id} удалён");
        await ReloadAccountsAsync();
    }

    private async Task SaveAccountAsync(bool add)
    {
        var profile = Editor.ToProfile();
        var gateway = Require();
        var info = add ? await gateway.AddAccountAsync(profile, _lifetime.Token) : await gateway.UpdateAccountAsync(profile, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        AddLog($"Профиль {(add ? "добавлен" : "сохранён")}: {info.Id} ({info.Provider})");
        await ReloadAccountsAsync();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == info.Id);
        await CheckSelectedAsync();
        await RefreshModelsAsync(false);
    }

    private async Task StartServerAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_serverStartup is { } pending)
        {
            await pending;
            return;
        }
        var startup = StartServerCoreAsync();
        _serverStartup = startup;
        try { await startup; }
        finally { if (ReferenceEquals(_serverStartup, startup)) _serverStartup = null; }
    }

    private async Task StartServerCoreAsync()
    {
        // A fresh dedicated in-memory key protects the local HTTP surface, including management.
        // It is not a provider credential and is never persisted or logged.
        var serverKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = await _startServer(EnsureLibrary(), EmbeddedUrl.Trim(),
            new GatewayServerOptions { ApiKey = serverKey, LoopbackOnly = true, SanitizeErrors = true }, _lifetime.Token);
        if (_stopping)
        {
            _retiringServer = server;
            await server.DisposeAsync();
            _retiringServer = null;
            return;
        }
        _server = server;
        ApiKey = serverKey;
        ApiUrl = _server.Url;
        Raise(nameof(ServerState));
        AddLog($"Встроенный сервер запущен: {_server.Url}. Выберите «OpenAI API» и «Подключить», чтобы работать через HTTP.");
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        await _server.DisposeAsync();
        _server = null;
        if (_stopping) return;
        Raise(nameof(ServerState));
        AddLog("Встроенный сервер остановлен");
    }

    private async Task SendAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_chatOperation is { } pending)
        {
            await pending;
            return;
        }
        var sending = SendCoreAsync();
        _chatOperation = sending;
        try { await sending; }
        finally { if (ReferenceEquals(_chatOperation, sending)) _chatOperation = null; }
    }

    private async Task SendCoreAsync()
    {
        var gateway = Require();
        var text = Input.Trim();
        Input = string.Empty;
        Messages.Add(new ChatMessageViewModel(ChatRole.User, text));
        var chatCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _chatCancellation = chatCancellation;
        IsChatting = true;
        try
        {
            for (var round = 0; round < 4; round++)
            {
                var request = BuildRequest();
                var assistant = new ChatMessageViewModel(ChatRole.Assistant, string.Empty);
                Messages.Add(assistant);
                var result = await RunTurnAsync(gateway, request, assistant, chatCancellation.Token);
                if (_stopping || result is null || result.ToolCalls.Count == 0) return;
                assistant.ToolCalls = [.. result.ToolCalls];
                assistant.Content = result.Content ?? string.Empty;
                assistant.Header = "Ассистент → вызов функции: " + string.Join(", ", result.ToolCalls.Select(c => $"{c.Name}({c.ArgumentsJson})"));
                foreach (var call in result.ToolCalls)
                {
                    var output = ExecuteDemoTool(call);
                    Messages.Add(new ChatMessageViewModel(ChatRole.Tool, output, $"Функция {call.Name}({call.ArgumentsJson})")
                    {
                        ToolCallId = call.Id,
                        ToolName = call.Name
                    });
                }
            }
            if (_stopping) return;
            const string exhaustion = "Достигнут лимит последовательных вызовов инструментов (4). Ответ ассистента не получен.";
            Messages.Add(new ChatMessageViewModel(ChatRole.Assistant, exhaustion)
            {
                IsError = true,
                IncludeInHistory = false,
                Footer = "Цикл инструментов завершён по лимиту"
            });
            Status = exhaustion;
            AddLog(exhaustion);
        }
        finally
        {
            if (_stopping) _isChatting = false;
            else IsChatting = false;
            chatCancellation.Dispose();
            if (ReferenceEquals(_chatCancellation, chatCancellation)) _chatCancellation = null;
        }
    }

    private async Task<ChatResult?> RunTurnAsync(ILlmGateway gateway, ChatRequest request, ChatMessageViewModel assistant, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Status = $"Запрос к {request.Model}…";
        try
        {
            ChatResult result;
            if (Stream)
            {
                ChatResult? completed = null;
                await foreach (var update in gateway.StreamAsync(request, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (update.Kind)
                    {
                        case ChatUpdateKind.Started:
                            assistant.Footer = $"{update.Provider} · {update.AccountId} · ожидание ответа…";
                            break;
                        case ChatUpdateKind.TextDelta:
                            assistant.Content += update.Text;
                            break;
                        case ChatUpdateKind.ReasoningDelta:
                            assistant.Reasoning += update.Text;
                            break;
                        case ChatUpdateKind.Completed:
                            completed = update.Result;
                            break;
                    }
                }
                result = completed ?? throw new GatewayException(GatewayErrorKind.Upstream, "Поток завершился без результата.");
            }
            else
            {
                result = await gateway.CompleteAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                assistant.Content = result.Content ?? string.Empty;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var usage = result.Usage;
            assistant.Footer = $"{result.Provider} · {result.AccountId} · {result.NativeModel} · {watch.Elapsed.TotalSeconds:0.0} с · " +
                $"токены {usage.PromptTokens}+{usage.CompletionTokens}{(usage.Estimated ? " (оценка)" : string.Empty)} · {result.FinishReason}";
            SetIdleStatus();
            if (result.ToolCalls.Count == 0 && string.IsNullOrEmpty(assistant.Content)) assistant.Content = result.Content ?? string.Empty;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_stopping) return null;
            assistant.Footer = "Остановлено пользователем";
            assistant.IncludeInHistory = assistant.Content.Length > 0;
            Status = "Остановлено";
            return null;
        }
        catch (OperationCanceledException)
        {
            if (_stopping) return null;
            // HttpClient also cancels when its deadline expires. Only the caller's
            // canceled token establishes that the user stopped this turn.
            var timeout = new GatewayException(GatewayErrorKind.Timeout, "HTTP request deadline expired.");
            assistant.IsError = true;
            assistant.IncludeInHistory = false;
            assistant.Content = GatewayUiDiagnostics.Describe(timeout);
            assistant.Footer = "Ошибка Timeout";
            Status = "Ошибка";
            AddLog($"{request.Model}: Timeout: {GatewayUiDiagnostics.Describe(timeout)}");
            return null;
        }
        catch (GatewayException ex)
        {
            if (_stopping) throw;
            assistant.IsError = true;
            assistant.IncludeInHistory = false;
            assistant.Content = GatewayUiDiagnostics.Describe(ex);
            assistant.Footer = $"Ошибка {ex.Kind}{(ex.RetryAt is { } retry ? $" · повтор после {retry.ToLocalTime():HH:mm}" : string.Empty)}";
            Status = "Ошибка";
            AddLog($"{request.Model}: {ex.Kind}: {GatewayUiDiagnostics.Describe(ex)}");
            if (ReferenceEquals(gateway, _gateway) && ex.Kind is GatewayErrorKind.RateLimited or GatewayErrorKind.AuthenticationRequired)
                await ReloadAccountsAsync(gateway);
            return null;
        }
    }

    private ChatRequest BuildRequest()
    {
        var request = new ChatRequest
        {
            Model = SelectedModel?.Id ?? "auto",
            AccountId = SelectedModel?.AccountId,
            ReasoningEffort = string.IsNullOrWhiteSpace(ReasoningEffort) || ReasoningEffort == DefaultEffort ? null : ReasoningEffort,
            Stop = string.IsNullOrEmpty(StopSequence) ? null : [StopSequence],
            ResponseFormat = JsonMode ? new ResponseFormat(ResponseFormatKind.JsonObject) : null,
            Tools = UseDemoTool ? [DemoTool] : null,
            ToolChoice = UseDemoTool ? "auto" : null
        };
        if (!string.IsNullOrWhiteSpace(SystemPrompt)) request.Messages.Add(ChatMessage.System(SystemPrompt.Trim()));
        foreach (var message in Messages.Where(m => m.IncludeInHistory && !m.IsError && (m.Content.Length > 0 || m.ToolCalls is { Count: > 0 })))
            request.Messages.Add(message.ToMessage());
        return request;
    }

    private static string ExecuteDemoTool(ToolCall call)
    {
        if (call.Name != DemoTool.Name) return JsonSerializer.Serialize(new { error = $"Неизвестная функция {call.Name}" }, GatewayJson.Options);
        var utc = false;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            utc = document.RootElement.TryGetProperty("utc", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
        }
        var now = utc ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        return JsonSerializer.Serialize(new { now = now.ToString("O"), timezone = utc ? "UTC" : TimeZoneInfo.Local.DisplayName }, GatewayJson.Options);
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (_stopping) return;
        foreach (var card in Quotas)
            foreach (var bucket in card.Buckets) bucket.Tick();
        if (!AutoRefreshQuotas || _gateway is null || IsChatting) return;
        if (--_quotaCountdown > 0) return;
        _quotaCountdown = 60;
        RefreshQuotasCommand.Execute(null);
    }

    private Dictionary<string, AccountInfo> AccountsById() =>
        Accounts.Where(a => a.Id is not null).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());

    private ILlmGateway Require()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        return _gateway ?? throw new InvalidOperationException("Сначала нажмите «Подключить».");
    }

    private void SetIdleStatus() => Status = _gateway?.AccountStoreWarning ?? "Готово";

    private void ReportError(Exception ex)
    {
        if (_stopping) return;
        var message = GatewayUiDiagnostics.Describe(ex);
        Status = "Ошибка: " + message;
        AddLog("Ошибка: " + message);
    }

    private void AddLog(string line)
    {
        if (_stopping) return;
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new ValueTask(_disposal);
        _stopping = true;
        _clock.Stop();
        _clock.Tick -= OnClockTick;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        _disposal = DisposeCoreAsync();
        return new ValueTask(_disposal);
    }

    private async Task DisposeCoreAsync()
    {
        var admitted = _admittedCommands.ToArray();
        _lifetime.Cancel();
        _chatCancellation?.Cancel();
        var failures = new List<Exception>();
        if (_serverStartup is { } startup)
        {
            await JoinAsync(startup);
        }
        if (_chatOperation is { } chat)
        {
            await JoinAsync(chat);
        }
        // Only top-level commands enter the registry. Their nested calls belong to the same task,
        // so shutdown waits for all admitted work without creating a nested-drain dependency.
        // Completed ordinary command failures belong to the command's error channel. Startup and
        // chat failures were joined separately above and still prevent a graceful shutdown claim.
        await JoinAsync(Task.WhenAll(admitted), reportFailure: false);
        if (_server is { } server)
            await CleanupAsync(async () => { await server.DisposeAsync(); _server = null; });
        if (_retiringServer is { } retiring)
            await CleanupAsync(async () => { await retiring.DisposeAsync(); _retiringServer = null; });
        await CleanupAsync(() => { _remote?.Dispose(); _remote = null; return Task.CompletedTask; });
        await CleanupAsync(() => { _library?.Dispose(); _library = null; return Task.CompletedTask; });
        await CleanupAsync(() => { _lifetime.Dispose(); return Task.CompletedTask; });
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Завершение допущенных операций не подтверждено.", failures.Distinct());

        async Task JoinAsync(Task operation, bool reportFailure = true)
        {
            try { await operation; }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!reportFailure) return;
                if (operation.Exception is { } aggregate) failures.AddRange(aggregate.InnerExceptions);
                else failures.Add(ex);
            }
        }

        async Task CleanupAsync(Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception ex) { failures.Add(ex); }
        }
    }

    private RelayCommand CreateCommand(Func<object?, Task> action, Action<Exception> onError,
        Func<object?, bool>? canExecute = null, bool retiresConnection = false,
        bool allowDuringConnectionChange = false) => new(
            parameter => RunAdmittedCommandAsync(() => action(parameter), retiresConnection, allowDuringConnectionChange), onError,
            parameter => !_stopping && (!_changingConnection || allowDuringConnectionChange)
                && (canExecute?.Invoke(parameter) ?? true));

    private async Task RunAdmittedCommandAsync(Func<Task> action, bool retiresConnection, bool allowDuringConnectionChange)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_changingConnection && !allowDuringConnectionChange)
            throw new InvalidOperationException("Дождитесь завершения подключения.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before invoking the command, including a synchronous callback that starts shutdown.
        _admittedCommands.Add(completion.Task);
        if (retiresConnection)
        {
            _changingConnection = true;
            _connectionCommand = completion.Task;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        try
        {
            await action();
            completion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            // The command also propagates the failure to RelayCommand; observe this retained drain copy.
            _ = completion.Task.Exception;
            throw;
        }
        finally
        {
            _admittedCommands.Remove(completion.Task);
            if (retiresConnection)
            {
                _connectionCommand = null;
                _changingConnection = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
