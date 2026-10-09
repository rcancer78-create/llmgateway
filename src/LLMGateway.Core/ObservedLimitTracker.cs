using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Core;

/// <summary>
/// Tracks observed rate limits and transient error states reported during execution.
/// Thread-safe and maintains account status overlays.
/// </summary>
public sealed class ObservedLimitTracker
{
    /// <summary>Used when the client reports a limit without a reset time.</summary>
    public const int DefaultLifetimeMinutes = 15;

    private readonly ConcurrentDictionary<string, AccountStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Message, DateTimeOffset At, DateTimeOffset? RetryAt)> _observedLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private readonly object _gate = new();

    public ObservedLimitTracker(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets the current account status overlay if recorded.
    /// </summary>
    public AccountStatus? Status(AccountProfile account)
    {
        lock (_gate)
        {
            PurgeExpiredLimit(account.Id);
            return _statuses.TryGetValue(account.Id, out var status) ? status : null;
        }
    }

    /// <summary>
    /// Sets account status, preserving active limits on ready or inconclusive probes.
    /// </summary>
    public void SetStatus(AccountProfile account, AccountStatus status)
    {
        lock (_gate)
        {
            if (status.Availability == AccountAvailability.Unknown
                && _statuses.TryGetValue(account.Id, out var existing)
                && existing.Availability == AccountAvailability.AuthenticationRequired)
            {
                return;
            }
            _statuses[account.Id] = PreserveObservedLimitOnReady(account, status);
        }
    }

    /// <summary>
    /// Tries to get active observed limit details.
    /// </summary>
    public bool TryGetObservedLimit(string accountId, out (string Message, DateTimeOffset At, DateTimeOffset? RetryAt) limit)
    {
        lock (_gate)
        {
            PurgeExpiredLimit(accountId);
            return _observedLimits.TryGetValue(accountId, out limit);
        }
    }

    private void PurgeExpiredLimit(string accountId)
    {
        lock (_gate)
        {
            if (!_observedLimits.TryGetValue(accountId, out var observed)) return;
            var deadline = observed.RetryAt ?? observed.At.AddMinutes(DefaultLifetimeMinutes);
            if (deadline > DateTimeOffset.UtcNow) return;
            _observedLimits.TryRemove(accountId, out _);
            if (_statuses.TryGetValue(accountId, out var status) && status.Availability == AccountAvailability.RateLimited)
                _statuses.TryRemove(accountId, out _);
        }
    }

    /// <summary>
    /// Records a native failure for an account, updating observed rate limit or auth required state.
    /// </summary>
    public void RecordFailure(AccountProfile account, NativeChatEvent failure)
    {
        lock (_gate)
        {
            if (failure.Kind != NativeChatEventKind.Error) return;
            var message = string.IsNullOrWhiteSpace(failure.Text) ? "Ошибка выполнения нативного клиента." : failure.Text;

            if (failure.ErrorKind == GatewayErrorKind.RateLimited)
            {
                var now = DateTimeOffset.UtcNow;
                var identity = Status(account)?.Identity;
                if (failure.RetryAt is { } reset && reset <= now)
                {
                    // An expired reset cannot establish an active cooldown or a permanent limit.
                    // Keep readiness unknown until an authoritative probe or chat succeeds.
                    _observedLimits.TryRemove(account.Id, out _);
                    _statuses[account.Id] = new AccountStatus(AccountAvailability.Unknown, identity, null,
                        "Native limit reset has already expired; refresh account status.", now);
                }
                else
                {
                    var retryAt = failure.RetryAt ?? now.AddMinutes(DefaultLifetimeMinutes);
                    _observedLimits[account.Id] = (message, now, retryAt);
                    _statuses[account.Id] = new AccountStatus(AccountAvailability.RateLimited, identity, null, message, now);
                }
            }
            else if (failure.ErrorKind == GatewayErrorKind.AuthenticationRequired)
            {
                _statuses[account.Id] = new AccountStatus(AccountAvailability.AuthenticationRequired, null, null, message, DateTimeOffset.UtcNow);
            }

        }

        _logger.LogWarning("Native chat failed for {Provider}: {Kind}", account.Provider, failure.ErrorKind);
    }

    /// <summary>
    /// Records a successful completion for an account, clearing or preserving active rate limits.
    /// </summary>
    /// <summary>
    /// A success clears the limit when this request was accepted after the limit was recorded.
    /// A request that was already in flight does not prove the quota recovered.
    /// </summary>
    public void RecordSuccess(AccountProfile account, bool isCurrentObservation, DateTimeOffset? acceptedAt)
    {
        lock (_gate)
        {
            if (!isCurrentObservation) return;

            if (_observedLimits.TryGetValue(account.Id, out var observed))
            {
                var deadline = observed.RetryAt ?? observed.At.AddMinutes(DefaultLifetimeMinutes);
                var stillActive = deadline > DateTimeOffset.UtcNow;
                var startedBeforeLimit = acceptedAt is null || acceptedAt <= observed.At;
                if (stillActive && startedBeforeLimit)
                {
                    _statuses.AddOrUpdate(account.Id,
                        _ => new AccountStatus(AccountAvailability.RateLimited, null, null, observed.Message, DateTimeOffset.UtcNow),
                        (_, old) => old with { Availability = AccountAvailability.RateLimited, Message = observed.Message, CheckedAt = DateTimeOffset.UtcNow });
                    return;
                }
            }

            _observedLimits.TryRemove(account.Id, out _);
            MarkReady(account);
        }
    }

    /// <summary>
    /// Updates status to Ready.
    /// </summary>
    public void MarkReady(AccountProfile account)
    {
        lock (_gate)
        {
            _statuses.AddOrUpdate(account.Id,
                _ => new AccountStatus(AccountAvailability.Ready, null, null, null, DateTimeOffset.UtcNow),
                (_, old) => old with { Availability = AccountAvailability.Ready, Message = null, CheckedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// Preserves an active native limit on ready, unknown or transient-error probes.
    /// </summary>
    public AccountStatus PreserveObservedLimitOnReady(AccountProfile account, AccountStatus status)
    {
        lock (_gate)
        {
            if (status.Availability is not (AccountAvailability.Ready or AccountAvailability.Unknown or AccountAvailability.Error)
                || !_observedLimits.TryGetValue(account.Id, out var observed)) return status;
            if (observed.RetryAt is { } retry && retry <= DateTimeOffset.UtcNow)
            {
                _observedLimits.TryRemove(account.Id, out _);
                return status;
            }
            return status with { Availability = AccountAvailability.RateLimited, Message = observed.Message };
        }
    }

    /// <summary>
    /// Invalidates recorded limits and status for a given account.
    /// </summary>
    public void Invalidate(string accountId)
    {
        lock (_gate)
        {
            _statuses.TryRemove(accountId, out _);
            _observedLimits.TryRemove(accountId, out _);
        }
    }
}
