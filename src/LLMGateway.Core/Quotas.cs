namespace LLMGateway.Core;

public sealed record QuotaBucket(
    string Name,
    string Label,
    double? UsedPercent,
    double? Used = null,
    double? Limit = null,
    string? Unit = null,
    int? WindowMinutes = null,
    DateTimeOffset? ResetsAt = null)
{
    public double? RemainingPercent => UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    public double? Remaining => Limit is { } limit && Used is { } used ? Math.Max(0, limit - used) : null;

    /// <summary>Time left until the bucket resets, computed at the moment of reading.</summary>
    public TimeSpan? ResetIn => ResetsAt is { } reset ? Max(reset - DateTimeOffset.UtcNow) : null;

    private static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

public sealed record QuotaSnapshot(
    string AccountId,
    ProviderKind Provider,
    DateTimeOffset FetchedAt,
    AccountAvailability Availability,
    bool Supported,
    string? Plan,
    IReadOnlyList<QuotaBucket> Buckets,
    string Source,
    string? Message,
    bool IsStale = false)
{
    public static QuotaSnapshot Unsupported(AccountProfile account, AccountAvailability availability, string? plan, string source, string message) =>
        new(account.Id, account.Provider, DateTimeOffset.UtcNow, availability, false, plan, [], source, message);
}
