using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ObservedLimitTrackerStateReviewTests
{
    private static AccountProfile Account => new() { Id = "owned", Provider = ProviderKind.Codex };

    [Fact]
    public void AlreadyExpiredNativeResetCannotCreateAStickyActiveLimit()
    {
        var tracker = new ObservedLimitTracker();
        var before = DateTimeOffset.UtcNow;
        tracker.RecordFailure(Account, NativeChatEvent.Fail(GatewayErrorKind.RateLimited,
            "observed limit", before.AddMinutes(-1)));
        Assert.Equal(AccountAvailability.Unknown, tracker.Status(Account)!.Availability);
        Assert.False(tracker.TryGetObservedLimit(Account.Id, out _));
        tracker.RecordSuccess(Account, isCurrentObservation: true, acceptedAt: before);
        Assert.Equal(AccountAvailability.Ready, tracker.Status(Account)!.Availability);
    }

    [Theory]
    [InlineData(AccountAvailability.Ready)]
    [InlineData(AccountAvailability.Unknown)]
    [InlineData(AccountAvailability.Error)]
    public void InconclusiveProbeCannotEraseAnUnexpiredObservedNativeLimit(AccountAvailability probe)
    {
        var tracker = new ObservedLimitTracker();
        tracker.RecordFailure(Account, NativeChatEvent.Fail(GatewayErrorKind.RateLimited,
            "observed limit", DateTimeOffset.UtcNow.AddHours(1)));
        tracker.SetStatus(Account, new AccountStatus(probe, null, null, "probe", DateTimeOffset.UtcNow));
        Assert.Equal(AccountAvailability.RateLimited, tracker.Status(Account)!.Availability);
        Assert.True(tracker.TryGetObservedLimit(Account.Id, out _));
    }

    [Fact]
    public void DefiniteAuthenticationFailureStillOverridesObservedLimit()
    {
        var tracker = new ObservedLimitTracker();
        tracker.RecordFailure(Account, NativeChatEvent.Fail(GatewayErrorKind.RateLimited,
            "observed limit", DateTimeOffset.UtcNow.AddHours(1)));
        tracker.SetStatus(Account, new AccountStatus(AccountAvailability.AuthenticationRequired,
            null, null, "sign in", DateTimeOffset.UtcNow));
        Assert.Equal(AccountAvailability.AuthenticationRequired, tracker.Status(Account)!.Availability);
    }
}
