using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ObservedLimitExpiryReviewTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("auth")]
    [InlineData("native-limit")]
    public async Task CachedQuotaExpiryRetiresOnlyItsExactObservedLimitStatus(string subsequentEvidence)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-observed-expiry-");
        var adapter = new ExpiringAdapter(subsequentEvidence);
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new() { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]), [adapter],
                new GatewayOptions { WorkspaceDirectory = root.FullName, QuotaCacheSeconds = 60 });
            var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(new ChatRequest
            { Model = "codex/owned/model", Messages = [ChatMessage.User("Owned expiration observation")] }));
            Assert.Equal(GatewayErrorKind.RateLimited, failure.Kind);
            Assert.Equal(adapter.RetryAt, failure.RetryAt);
            var initial = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));
            Assert.Equal(adapter.RetryAt, Assert.Single(initial.Buckets, item => item.Name == "observed").ResetsAt);
            Assert.Equal(1, adapter.QuotaCalls);
            if (subsequentEvidence == "auth")
            {
                adapter.Status = AccountAvailability.AuthenticationRequired;
                await gateway.CheckAccountAsync("owned");
                Assert.Equal(AccountAvailability.AuthenticationRequired, Assert.Single(await gateway.GetAccountsAsync()).Availability);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (DateTimeOffset.UtcNow <= adapter.RetryAt) await Task.Delay(25, deadline.Token);
            var cached = Assert.Single(await gateway.GetQuotasAsync(false, "owned"));
            Assert.DoesNotContain(cached.Buckets, item => item.Name == "observed");
            Assert.Equal(1, adapter.QuotaCalls); // Cached expiry must not be hidden by an extra native probe.
            var current = Assert.Single(await gateway.GetAccountsAsync());
            if (subsequentEvidence == "ready")
            {
                Assert.Equal(AccountAvailability.Ready, cached.Availability);
                Assert.Equal(AccountAvailability.Ready, current.Availability);
            }
            else if (subsequentEvidence == "auth") Assert.Equal(AccountAvailability.AuthenticationRequired, current.Availability);
            else
            {
                Assert.Equal(AccountAvailability.RateLimited, cached.Availability);
                Assert.Equal(AccountAvailability.RateLimited, current.Availability);
                Assert.Contains(cached.Buckets, item => item.Name == "native");
            }
        }
        finally { root.Delete(true); }
    }

    private sealed class ExpiringAdapter(string subsequentEvidence) : IProviderAdapter
    {
        public DateTimeOffset RetryAt { get; private set; }
        public int QuotaCalls { get; private set; }
        public AccountAvailability Status { get; set; } = AccountAvailability.Ready;
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(true, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(new AccountStatus(Status, null, "owned", "Owned status", DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Owned", true)]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile, CancellationToken token)
        {
            QuotaCalls++;
            return Task.FromResult(new QuotaSnapshot(profile.Id, profile.Provider, DateTimeOffset.UtcNow,
                subsequentEvidence == "native-limit" ? AccountAvailability.RateLimited : AccountAvailability.Ready,
                true, null, subsequentEvidence == "native-limit"
                    ? [new QuotaBucket("native", "Owned genuine adapter limit", 100, ResetsAt: DateTimeOffset.UtcNow.AddMinutes(5))] : [], "owned", "Owned quota"));
        }
        public Task StartInteractiveLoginAsync(AccountProfile profile, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            RetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "Owned expiring limit", RetryAt);
        }
    }
}
