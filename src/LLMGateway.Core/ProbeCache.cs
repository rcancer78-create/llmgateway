using System.Collections.Concurrent;

namespace LLMGateway.Core;

/// <summary>
/// Caches models and quotas probed from native provider adapters with TTL expiration.
/// </summary>
public sealed class ProbeCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<NativeModel> Models)> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _quotas = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetModels(string accountId, TimeSpan ttl, out IReadOnlyList<NativeModel>? models)
    {
        if (_models.TryGetValue(accountId, out var entry) && DateTimeOffset.UtcNow - entry.At < ttl)
        {
            models = entry.Models;
            return true;
        }
        models = null;
        return false;
    }

    public void SetModels(string accountId, IReadOnlyList<NativeModel> models)
    {
        _models[accountId] = (DateTimeOffset.UtcNow, models);
    }

    public bool TryGetQuota(string accountId, TimeSpan ttl, out QuotaSnapshot? quota)
    {
        if (_quotas.TryGetValue(accountId, out var existing) && DateTimeOffset.UtcNow - existing.FetchedAt < ttl)
        {
            quota = existing;
            return true;
        }
        quota = null;
        return false;
    }

    public void SetQuota(string accountId, QuotaSnapshot quota)
    {
        _quotas[accountId] = quota;
    }

    public void Invalidate(string? accountId = null)
    {
        if (accountId is not null)
        {
            _models.TryRemove(accountId, out _);
            _quotas.TryRemove(accountId, out _);
        }
        else
        {
            _models.Clear();
            _quotas.Clear();
        }
    }
}
