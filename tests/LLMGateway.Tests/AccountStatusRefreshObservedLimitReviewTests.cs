using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class AccountStatusRefreshObservedLimitReviewTests
{
    [Fact]
    public async Task AReadyStatusProbeCannotEraseAnUnexpiredNativeLimitFromPublicAccountStatus()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-quota-limit-");
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new() { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]),
                [new OwnedAdapter(retryAt)], new GatewayOptions { WorkspaceDirectory = root.FullName });
            var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(
                new ChatRequest { Model = "codex/owned/model", Messages = [ChatMessage.User("Owned limit observation")] }));
            Assert.Equal(GatewayErrorKind.RateLimited, failure.Kind);
            Assert.Equal(retryAt, failure.RetryAt);
            Assert.Equal(AccountAvailability.RateLimited, Assert.Single(await gateway.GetAccountsAsync()).Availability);

            var checkedAccount = await gateway.CheckAccountAsync("owned");
            Assert.Equal(AccountAvailability.RateLimited, checkedAccount.Availability);
            Assert.Equal(AccountAvailability.RateLimited, Assert.Single(await gateway.GetAccountsAsync()).Availability);
            var quota = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));

            Assert.Equal(AccountAvailability.RateLimited, quota.Availability);
            Assert.Equal(retryAt, Assert.Single(quota.Buckets, bucket => bucket.Name == "observed").ResetsAt);
            Assert.Equal(AccountAvailability.RateLimited, Assert.Single(await gateway.GetAccountsAsync()).Availability);
        }
        finally { root.Delete(true); }
    }

    private sealed class OwnedAdapter(DateTimeOffset retryAt) : IProviderAdapter
    {
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(true, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Owned", true)]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile, CancellationToken token) =>
            Task.FromResult(new QuotaSnapshot(profile.Id, profile.Provider, DateTimeOffset.UtcNow,
                AccountAvailability.Ready, true, null, [], "owned", null));
        public Task StartInteractiveLoginAsync(AccountProfile profile, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "Owned native limit", retryAt);
        }
    }
}
