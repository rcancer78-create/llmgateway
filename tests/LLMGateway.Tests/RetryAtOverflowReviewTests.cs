using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class RetryAtOverflowReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("try again in 99999999999 days")]
    [InlineData("try again in 6000000 days and 6000000 days")]
    public void UnrepresentableRelativeDurationIsUnknown(string message)
        => Assert.Null(NativeErrorClassifier.ParseRetryAt(message, Now));

    [Fact]
    public void NonFiniteRelativeNumberIsUnknown()
        => Assert.Null(NativeErrorClassifier.ParseRetryAt("try again in " + new string('9', 400) + " days", Now));

    [Fact]
    public void RepresentableDurationBeyondTimestampRangeIsUnknown()
        => Assert.Null(NativeErrorClassifier.ParseRetryAt("try again in 1s", DateTimeOffset.MaxValue));

    [Fact]
    public void OrdinaryDecimalCompoundDurationRetainsExactResult()
        => Assert.Equal(Now.AddMinutes(92), NativeErrorClassifier.ParseRetryAt("try again in 1.5 hours and 2 minutes", Now));

    [Fact]
    public void ZeroDurationRemainsUnknown()
        => Assert.Null(NativeErrorClassifier.ParseRetryAt("try again in 0s", Now));

    [Fact]
    public void AbsoluteResetRetainsPriorityOverUnrepresentableRelativeText()
        => Assert.Equal(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero),
            NativeErrorClassifier.ParseRetryAt("resets at 2026-10-09T00:00:00Z; try again in 99999999999 days", Now));
}
