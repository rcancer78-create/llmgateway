using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Core;

public sealed class LlmGateway : ILlmGateway, IDisposable
{
    private readonly IAccountStore _store;
    private readonly IReadOnlyDictionary<ProviderKind, IProviderAdapter> _adapters;
    private readonly GatewayOptions _options;
    private readonly ILogger _logger;
    private readonly ObservedLimitTracker _limitTracker;
    private readonly ProbeCache _probeCache = new();
    private readonly AccountRequestSlots _slots = new();
    private readonly AccountRequestSlots _probes = new();
    private readonly object _observationGate = new();
    private readonly object _catalogRefreshGate = new();
    private readonly Dictionary<string, object> _observationGenerations = new(StringComparer.OrdinalIgnoreCase);
    private Task? _catalogRefreshTask;
    private DateTimeOffset _catalogRefreshFinishedAt = DateTimeOffset.MinValue;
    private const int MaxConcurrentProbes = 4;

    public string? AccountStoreWarning => _store.PersistenceWarning;

    public LlmGateway(IAccountStore store, IEnumerable<IProviderAdapter> adapters, GatewayOptions options, ILogger<LlmGateway>? logger = null)
    {
        _store = store;
        _adapters = adapters.ToDictionary(a => a.Provider);
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _limitTracker = new ObservedLimitTracker(_logger);
        _options.CredentialVariableNames ??= () => _store.GetAll()
            .Select(account => account.ApiKeyVariable)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);
    }

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderInfo> result = _adapters.Values.OrderBy(a => a.Provider).Select(adapter =>
        {
            var path = adapter.ResolveExecutable(new AccountProfile { Provider = adapter.Provider });
            return new ProviderInfo(adapter.Provider, adapter.DisplayName, adapter.DefaultExecutable, path is not null, path, adapter.Capabilities);
        }).ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AccountInfo> result = _store.GetAll().Select(ToInfo).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        profile = profile.Freeze();
        EnsureAdapter(profile.Provider);
        await _store.AddAsync(profile, cancellationToken).ConfigureAwait(false);
        Invalidate(profile.Id);
        return ToInfo(_store.Find(profile.Id)!);
    }

    public async Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        profile = profile.Freeze();
        EnsureAdapter(profile.Provider);
        await _store.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        Invalidate(profile.Id);
        return ToInfo(_store.Find(profile.Id)!);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _store.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
        Invalidate(accountId);
        _slots.Retire(accountId);
    }

    public async Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _store.SelectAsync(accountId, cancellationToken).ConfigureAwait(false);
        return ToInfo(Require(accountId));
    }

    public async Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = Require(accountId);
        await RefreshStatusAsync(account, cancellationToken).ConfigureAwait(false);
        return ToInfo(account);
    }

    public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = Require(accountId);
        _limitTracker.Invalidate(account.Id);
        return Adapter(account).StartInteractiveLoginAsync(account, cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var accounts = Select(accountId).Where(a => a.Enabled && _adapters.TryGetValue(a.Provider, out var adapter) && adapter.ResolveExecutable(a) is not null).ToList();
        var ttl = TimeSpan.FromSeconds(_options.ModelCacheSeconds);
        await Task.WhenAll(accounts.Select(async account =>
        {
            if (!refresh && _probeCache.TryGetModels(account.Id, ttl, out _)) return;
            var observation = CaptureObservation(account);
            using var probe = await _probes.AcquireAsync("catalog-and-quota", MaxConcurrentProbes, cancellationToken).ConfigureAwait(false);
            var adapter = Adapter(account);
            try
            {
                var models = await adapter.ListModelsAsync(account, cancellationToken).ConfigureAwait(false);
                lock (_observationGate)
                    if (models.Count > 0 && IsCurrentObservation(account.Id, observation))
                        _probeCache.SetModels(account.Id, models);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Model discovery failed for {Provider} ({ExceptionType})", account.Provider, ex.GetType().Name);
            }
        })).ConfigureAwait(false);

        return accounts.SelectMany(account =>
        {
            var models = _probeCache.TryGetModels(account.Id, ttl, out var cached) ? cached! : Fallback(account);
            return models.Select(m => new GatewayModel(ModelRouter.ModelId(account, m.Id), m.Id, m.DisplayName, account.Provider, account.Id, m.IsDefault));
        }).ToArray();
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var accounts = Select(accountId).ToList();
        var ttl = TimeSpan.FromSeconds(_options.QuotaCacheSeconds);
        var snapshots = await Task.WhenAll(accounts.Select(async account =>
        {
            if (!refresh && _probeCache.TryGetQuota(account.Id, ttl, out var cached) && cached is not null)
                return Overlay(account, cached);
            var observation = CaptureObservation(account);
            using var probe = await _probes.AcquireAsync("catalog-and-quota", MaxConcurrentProbes, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadQuotaAsync(account, observation, cancellationToken).ConfigureAwait(false);
            lock (_observationGate)
                if (!snapshot.IsStale && IsCurrentObservation(account.Id, observation)) _probeCache.SetQuota(account.Id, snapshot);
            return Overlay(account, snapshot);
        })).ConfigureAwait(false);
        return snapshots;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (update.Kind == ChatUpdateKind.Completed) return update.Result!;
        throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент завершился без ответа.");
    }

    public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count == 0) throw GatewayException.Invalid("Поле messages обязательно.");
        if (request.ResponseFormat?.Kind == ResponseFormatKind.JsonSchema)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Проверка JSON Schema не поддерживается нативными клиентами.");
        var route = await RouteAsync(request, cancellationToken).ConfigureAwait(false);
        var account = route.Account;
        // Capture the routed profile before preparation/dispatch awaits. Account mutations retire
        // observations from this generation, while the admitted request retains its own lifetime.
        var chatObservation = CaptureObservation(account);
        var adapter = Adapter(account);
        if (adapter.ResolveExecutable(account) is null)
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, $"{adapter.DisplayName}: нативный клиент '{account.Executable ?? adapter.DefaultExecutable}' не установлен или его лаунчер не поддерживается.");
        if (route.NativeModel is not null && !ModelRouter.IsValidModelName(route.NativeModel))
            throw GatewayException.Invalid($"Недопустимое имя модели '{route.NativeModel}'.");

        var prompt = PromptBuilder.Build(request, adapter.Capabilities.MaxPromptCharacters);
        var useTools = ToolCalling.IsEnabled(request);
        var jsonMode = request.ResponseFormat?.Kind is ResponseFormatKind.JsonObject or ResponseFormatKind.JsonSchema;
        var buffered = useTools || jsonMode;
        var workingDirectory = request.ExecutionContext is { } context
            ? context.WorkingDirectory : WorkingDirectory(account);
        var modelId = route.NativeModel is null
            ? $"{account.Provider.ToString().ToLowerInvariant()}/{account.Id}"
            : ModelRouter.ModelId(account, route.NativeModel);
        using var slot = await AcquireSlotAsync(account.Id, cancellationToken).ConfigureAwait(false);
        yield return new ChatUpdate(ChatUpdateKind.Started, AccountId: account.Id, Provider: account.Provider, Model: modelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSpan = TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds));
        timeout.CancelAfter(timeoutSpan);
        var text = new StringBuilder();
        // Tool JSON is parsed before stop sequences, so the live filter stays open while tools are enabled.
        var stop = new StopSequenceFilter(useTools ? null : request.Stop);
        TokenUsage? usage = null;
        string? finalText = null;
        var truncated = false;
        IAsyncEnumerator<NativeChatEvent>? events = null;
        try
        {
            var nativeRequest = new NativeChatRequest
            {
                Prompt = prompt,
                Model = route.NativeModel,
                ReasoningEffort = request.ReasoningEffort,
                WorkingDirectory = workingDirectory,
                Timeout = timeoutSpan
            };
            events = adapter.RunChatAsync(account, nativeRequest, timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new GatewayException(GatewayErrorKind.Timeout, $"Нативный клиент не ответил за {timeoutSpan.TotalSeconds:0} с.");
                }
                if (!moved) break;
                var item = events.Current;
                switch (item.Kind)
                {
                    case NativeChatEventKind.Text when !string.IsNullOrEmpty(item.Text):
                        var safe = stop.Push(item.Text);
                        var piece = OutputBudget.Fit(safe, text.Length, request.MaxOutputTokens, out var exhausted);
                        if (piece.Length > 0)
                        {
                            text.Append(piece);
                            if (!buffered) yield return new ChatUpdate(ChatUpdateKind.TextDelta, piece);
                        }
                        truncated |= exhausted;
                        break;
                    case NativeChatEventKind.Reasoning when !string.IsNullOrEmpty(item.Text):
                        yield return new ChatUpdate(ChatUpdateKind.ReasoningDelta, item.Text);
                        break;
                    case NativeChatEventKind.FinalText:
                        finalText = item.Text;
                        break;
                    case NativeChatEventKind.Usage:
                        usage = item.Usage;
                        break;
                    case NativeChatEventKind.Error:
                        var failure = item.ErrorKind == GatewayErrorKind.RateLimited && item.RetryAt is null
                            ? item with { RetryAt = DateTimeOffset.UtcNow.AddMinutes(ObservedLimitTracker.DefaultLifetimeMinutes) }
                            : item;
                        RecordFailure(account, failure, chatObservation);
                        throw new GatewayException(failure.ErrorKind, $"{adapter.DisplayName}: {NativeErrorClassifier.Trim(failure.Text)}") { RetryAt = failure.RetryAt };
                }
                if (stop.Stopped || truncated) break;
            }
        }
        finally
        {
            if (events is not null)
            {
                try { await events.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Cleanup must not replace a completed result or a classified provider error.
                    _logger.LogWarning("Native chat cleanup failed ({ExceptionType})", ex.GetType().Name);
                }
            }
        }

        var streamed = text.Length;
        var output = ChatOutputPipeline.Complete(text.ToString(), truncated, finalText, stop, request, useTools, jsonMode);
        if (!buffered && output.Content.Length > streamed)
            yield return new ChatUpdate(ChatUpdateKind.TextDelta, output.Content[streamed..]);
        var content = output.Content;
        var calls = output.Calls;
        truncated = output.Truncated;
        if (buffered && content.Length > 0) yield return new ChatUpdate(ChatUpdateKind.TextDelta, content);
        if (!truncated && content.Length == 0 && calls.Count == 0 && !output.Stopped)
            throw new GatewayException(GatewayErrorKind.Upstream, $"{adapter.DisplayName}: пустой ответ нативного клиента.");

        RecordSuccess(account, chatObservation);
        var finish = truncated ? "length" : calls.Count > 0 ? "tool_calls" : "stop";
        if (truncated && request.MaxOutputTokens is > 0 and var cap && usage is { } reported)
            usage = reported with { CompletionTokens = Math.Min(reported.CompletionTokens, cap), TotalTokens = reported.PromptTokens + Math.Min(reported.CompletionTokens, cap) };
        var result = new ChatResult(
            "chatcmpl-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            modelId,
            route.NativeModel ?? "default",
            account.Provider,
            account.Id,
            content.Length == 0 ? null : content,
            calls,
            finish,
            usage ?? TokenUsage.Estimate(prompt, content));
        yield return new ChatUpdate(ChatUpdateKind.Completed, Result: result, AccountId: account.Id, Provider: account.Provider, Model: modelId);
    }

    private async Task<RouteResult> RouteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var accounts = _store.GetAll();
        if (request.ExecutionContext is { } context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(context.Provider) || string.IsNullOrWhiteSpace(context.AccountId)
                || !ModelRouter.IsValidModelName(context.NativeModel)
                || context.NativeModel.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(context.WorkingDirectory) || !Directory.Exists(context.WorkingDirectory))
                throw GatewayException.Invalid("Некорректная привязка выполнения LLMGateway.");
            var matches = accounts.Where(a => a.Id.Equals(context.AccountId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Provider != context.Provider || !matches[0].Enabled)
                throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Привязанный аккаунт LLMGateway недоступен.");
            if (matches[0].ExtraArguments.Count != 0)
                throw GatewayException.Invalid("Дополнительные CLI-аргументы не поддерживаются для привязанного выполнения.");
            var expectedModel = ModelRouter.ModelId(matches[0], context.NativeModel);
            if (!string.Equals(request.AccountId, context.AccountId, StringComparison.OrdinalIgnoreCase) || request.Model != expectedModel)
                throw GatewayException.Invalid("Маршрут запроса не совпадает с привязкой выполнения.");
            return new RouteResult(matches[0], context.NativeModel);
        }
        IReadOnlyList<NativeModel>? Cached(string id) => _probeCache.TryGetModels(id, TimeSpan.FromSeconds(_options.ModelCacheSeconds), out var cached) ? cached : null;
        try
        {
            return ModelRouter.Resolve(request, accounts, _options.DefaultProvider, Cached);
        }
        catch (GatewayException ex) when (ex.Kind == GatewayErrorKind.ModelNotFound)
        {
            await RefreshUnknownModelCatalogAsync(cancellationToken).ConfigureAwait(false);
            accounts = _store.GetAll();
            return ModelRouter.Resolve(request, accounts, _options.DefaultProvider, Cached);
        }
    }

    private async Task<QuotaSnapshot> ReadQuotaAsync(AccountProfile account, Observation? observation, CancellationToken cancellationToken)
    {
        var adapter = Adapter(account);
        if (!account.Enabled)
            return QuotaSnapshot.Unsupported(account, AccountAvailability.Disabled, null, "gateway", "Аккаунт отключён.");
        if (adapter.ResolveExecutable(account) is null)
            return QuotaSnapshot.Unsupported(account, AccountAvailability.NotInstalled, null, "gateway", $"Клиент '{account.Executable ?? adapter.DefaultExecutable}' не установлен или его лаунчер не поддерживается.");
        try
        {
            var snapshot = await adapter.GetQuotaAsync(account, cancellationToken).ConfigureAwait(false);
            lock (_observationGate)
            {
                if (IsCurrentObservation(account.Id, observation))
                    _limitTracker.SetStatus(account, new AccountStatus(snapshot.Availability, snapshot.Plan is null ? null : new AccountIdentity(null, snapshot.Plan, null), null, snapshot.Message, DateTimeOffset.UtcNow));
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Quota read failed for {Provider} ({ExceptionType})", account.Provider, ex.GetType().Name);
            return _probeCache.TryGetQuota(account.Id, TimeSpan.FromSeconds(_options.QuotaCacheSeconds), out var old) && old is not null
                ? old with { IsStale = true, Message = NativeErrorClassifier.Trim(ex.Message) }
                : new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, AccountAvailability.Error, adapter.Capabilities.SupportsQuota, null, [], adapter.Capabilities.QuotaSource, NativeErrorClassifier.Trim(ex.Message));
        }
    }

    private QuotaSnapshot Overlay(AccountProfile account, QuotaSnapshot snapshot)
    {
        var status = _limitTracker.Status(account);
        if (status is not null && status.Availability == AccountAvailability.RateLimited && status.Message is not null)
        {
            var buckets = snapshot.Buckets.ToList();
            _limitTracker.TryGetObservedLimit(account.Id, out var observed);
            buckets.Add(new QuotaBucket("observed", "Лимит, сообщённый клиентом", 100, ResetsAt: observed.RetryAt));
            return snapshot with { Availability = AccountAvailability.RateLimited, Buckets = buckets, Message = status.Message };
        }
        return snapshot;
    }


































    private async Task RefreshStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var observation = CaptureObservation(account);
        var adapter = Adapter(account);
        AccountStatus status;
        if (!account.Enabled) status = new AccountStatus(AccountAvailability.Disabled, null, null, "Аккаунт отключён.", DateTimeOffset.UtcNow);
        else if (adapter.ResolveExecutable(account) is null)
            status = new AccountStatus(AccountAvailability.NotInstalled, null, null, $"Клиент '{account.Executable ?? adapter.DefaultExecutable}' не установлен или его лаунчер не поддерживается.", DateTimeOffset.UtcNow);
        else
        {
            try { status = await adapter.GetStatusAsync(account, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                status = new AccountStatus(AccountAvailability.Error, null, null, NativeErrorClassifier.Trim(ex.Message), DateTimeOffset.UtcNow);
            }
        }
        lock (_observationGate)
            if (IsCurrentObservation(account.Id, observation))
                _limitTracker.SetStatus(account, status);
    }

    private void RecordFailure(AccountProfile account, NativeChatEvent failure, Observation? observation)
    {

        lock (_observationGate)
        {
            if (IsCurrentObservation(account.Id, observation))
            {
                _limitTracker.RecordFailure(account, failure);








            }
        }

    }

    private void RecordSuccess(AccountProfile account, Observation? observation)
    {
        lock (_observationGate)
        {
            _limitTracker.RecordSuccess(account, IsCurrentObservation(account.Id, observation), observation?.AcceptedAt);










        }
    }





    private AccountInfo ToInfo(AccountProfile account)
    {
        var adapter = Adapter(account);
        var status = _limitTracker.Status(account);
        var availability = status?.Availability;
        if (availability is null or AccountAvailability.Unknown && _probeCache.TryGetQuota(account.Id, TimeSpan.FromSeconds(_options.QuotaCacheSeconds), out var cachedQuota) && cachedQuota is not null && cachedQuota.Availability != AccountAvailability.Unknown) availability = cachedQuota.Availability;
        availability ??= (!account.Enabled ? AccountAvailability.Disabled
                : adapter.ResolveExecutable(account) is null ? AccountAvailability.NotInstalled : AccountAvailability.Unknown);
        return new AccountInfo(account.Id, account.DisplayName, account.Provider, account.IsActive, account.Enabled, availability.Value,
            status?.Identity, account.Executable ?? adapter.DefaultExecutable, account.ConfigDirectory, account.AuthMode,
            account.ApiKeyVariable, account.WorkingDirectory, account.DefaultModel, status?.Message, status?.CheckedAt,
            AccountEnvironment.WithoutSecrets(account.Environment), account.ExtraArguments);
    }

    private IEnumerable<NativeModel> Fallback(AccountProfile account) =>
        account.DefaultModel is { Length: > 0 } model ? [new NativeModel(model, model, true)] : [new NativeModel("auto", "По умолчанию клиента", true)];

    private string WorkingDirectory(AccountProfile account)
    {
        var directory = string.IsNullOrWhiteSpace(account.WorkingDirectory) ? _options.ResolveWorkspace() : account.WorkingDirectory!;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private IEnumerable<AccountProfile> Select(string? accountId) => string.IsNullOrWhiteSpace(accountId)
        ? _store.GetAll()
        : [Require(accountId)];

    private AccountProfile Require(string accountId) => _store.Find(accountId)
        ?? throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{accountId}' не найден.");

    private IProviderAdapter Adapter(AccountProfile account) => EnsureAdapter(account.Provider);









    private IProviderAdapter EnsureAdapter(ProviderKind provider) => _adapters.TryGetValue(provider, out var adapter)
        ? adapter
        : throw new GatewayException(GatewayErrorKind.Unsupported, $"Адаптер для {provider} не зарегистрирован.");

    private void Invalidate(string accountId)
    {
        lock (_observationGate)
        {
            _observationGenerations.Remove(accountId);
            _limitTracker.Invalidate(accountId);
            _probeCache.Invalidate(accountId);



        }
    }

    private sealed record Observation(object Generation, DateTimeOffset AcceptedAt, string Profile);

    private static string ObservationProfile(AccountProfile account)
    {
        // Selection changes routing, not the identity of an already admitted request.
        var profile = account.Freeze();
        profile.IsActive = false;
        return JsonSerializer.Serialize(profile, GatewayJson.Options);
    }

    private Observation? CaptureObservation(AccountProfile account)
    {
        lock (_observationGate)
        {
            var current = _store.Find(account.Id);
            var profile = ObservationProfile(account);
            if (current is null || ObservationProfile(current) != profile) return null;
            if (!_observationGenerations.TryGetValue(account.Id, out var generation))
                _observationGenerations[account.Id] = generation = new object();
            return new Observation(generation, DateTimeOffset.UtcNow, profile);
        }
    }

    // Caller holds _observationGate so a successful mutation cannot invalidate between validation and publication.
    private bool IsCurrentObservation(string accountId, Observation? observation) =>
        observation is not null && _observationGenerations.TryGetValue(accountId, out var generation)
        && ReferenceEquals(generation, observation.Generation)
        && _store.Find(accountId) is { } current
        && ObservationProfile(current) == observation.Profile;


    private async Task<IDisposable> AcquireSlotAsync(string accountId, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitSpan = TimeSpan.FromSeconds(Math.Max(1, _options.SlotWaitSeconds));
        wait.CancelAfter(waitSpan);
        try
        {
            return await _slots.AcquireAsync(accountId, _options.MaxConcurrentRequestsPerAccount, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable,
                $"Очередь аккаунта '{accountId}' не освободилась за {waitSpan.TotalSeconds:0} с.")
            { RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, waitSpan.TotalSeconds)) };
        }
    }

    private Task RefreshUnknownModelCatalogAsync(CancellationToken cancellationToken)
    {
        Task refresh;
        lock (_catalogRefreshGate)
        {
            if (_catalogRefreshTask is { IsCompleted: false })
                refresh = _catalogRefreshTask;
            else if (DateTimeOffset.UtcNow - _catalogRefreshFinishedAt < TimeSpan.FromSeconds(Math.Max(1, _options.CatalogRefreshCooldownSeconds)))
                return Task.CompletedTask;
            else
                refresh = _catalogRefreshTask = RunCatalogRefreshAsync();
        }
        return refresh.WaitAsync(cancellationToken);
    }

    private async Task RunCatalogRefreshAsync()
    {
        try { await GetModelsAsync(refresh: true, accountId: null, CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            lock (_catalogRefreshGate)
            {
                _catalogRefreshFinishedAt = DateTimeOffset.UtcNow;
                _catalogRefreshTask = null;
            }
        }
    }

    public void Dispose()
    {
        _slots.Dispose();
        _probes.Dispose();
    }
}
