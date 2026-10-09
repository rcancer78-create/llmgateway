namespace LLMGateway.Core;

/// <summary>Reservations include waiters so retirement never disposes a semaphore still in use.</summary>
internal sealed class AccountRequestSlots : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public async Task<IDisposable> AcquireAsync(string accountId, int limit, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (_disposed) throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Шлюз остановлен.");
            if (!_entries.TryGetValue(accountId, out entry!))
                _entries.Add(accountId, entry = new Entry(Math.Max(1, limit)));
            entry.Reservations++;
        }
        try { await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { Release(accountId, entry, acquired: false); throw; }
        return new Lease(this, accountId, entry);
    }

    public void Retire(string accountId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(accountId, out var entry)) return;
            entry.Retired = true;
            RemoveIfIdle(accountId, entry);
        }
    }

    private void Release(string accountId, Entry entry, bool acquired)
    {
        lock (_gate)
        {
            if (acquired) entry.Semaphore.Release();
            entry.Reservations--;
            RemoveIfIdle(accountId, entry);
        }
    }

    private void RemoveIfIdle(string accountId, Entry entry)
    {
        if (!entry.Retired || entry.Reservations != 0) return;
        _entries.Remove(accountId);
        entry.Semaphore.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (id, entry) in _entries.ToArray())
            {
                entry.Retired = true;
                RemoveIfIdle(id, entry);
            }
        }
    }

    private sealed class Entry(int limit)
    {
        public SemaphoreSlim Semaphore { get; } = new(limit, limit);
        public int Reservations;
        public bool Retired;
    }

    private sealed class Lease(AccountRequestSlots owner, string accountId, Entry entry) : IDisposable
    {
        private AccountRequestSlots? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(accountId, entry, acquired: true);
    }
}
