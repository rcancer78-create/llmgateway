using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ConcurrentChatRateLimitReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentLateSuccessCannotEraseAnUnexpiredObservedRateLimit(bool alreadyExpired)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-limit-race-");
        var adapter = new HeldAdapter(DateTimeOffset.UtcNow.AddMinutes(alreadyExpired ? -1 : 30));
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new() { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]), [adapter],
                new GatewayOptions { WorkspaceDirectory = root.FullName, MaxConcurrentRequestsPerAccount = 2 });
            var request = new ChatRequest { Model = "codex/owned/model", Messages = [ChatMessage.User("Owned concurrent limit observation")] };
            var failing = gateway.CompleteAsync(request);
            await adapter.FailureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var succeeding = gateway.CompleteAsync(request);
            try
            {
                await adapter.SuccessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                adapter.ReleaseFailure.TrySetResult();
                var error = await Assert.ThrowsAsync<GatewayException>(() => failing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(GatewayErrorKind.RateLimited, error.Kind);
                Assert.Equal(adapter.RetryAt, error.RetryAt);
                Assert.Equal(alreadyExpired ? AccountAvailability.Unknown : AccountAvailability.RateLimited,
                    Assert.Single(await gateway.GetAccountsAsync()).Availability);
                adapter.ReleaseSuccess.TrySetResult();
                Assert.Equal("owned success", (await succeeding.WaitAsync(TimeSpan.FromSeconds(5))).Content);

                var account = Assert.Single(await gateway.GetAccountsAsync());
                var quota = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));
                if (alreadyExpired)
                {
                    Assert.Equal(AccountAvailability.Ready, account.Availability);
                    Assert.Equal(AccountAvailability.Ready, quota.Availability);
                    Assert.DoesNotContain(quota.Buckets, bucket => bucket.Name == "observed");
                }
                else
                {
                    Assert.Equal(AccountAvailability.RateLimited, account.Availability);
                    Assert.Equal(AccountAvailability.RateLimited, quota.Availability);
                    Assert.Equal(adapter.RetryAt, Assert.Single(quota.Buckets, bucket => bucket.Name == "observed").ResetsAt);
                }
                Assert.Equal(2, adapter.Calls);
            }
            finally
            {
                adapter.ReleaseFailure.TrySetResult(); adapter.ReleaseSuccess.TrySetResult();
                await Record.ExceptionAsync(() => failing.WaitAsync(TimeSpan.FromSeconds(5)));
                await Record.ExceptionAsync(() => succeeding.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally { root.Delete(true); }
    }

    private sealed class HeldAdapter(DateTimeOffset retryAt) : IProviderAdapter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public DateTimeOffset RetryAt => retryAt;
        public TaskCompletionSource FailureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SuccessEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSuccess { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(true, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, "owned", null, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Owned", true)]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult(new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, AccountAvailability.Ready, true, null, [], "owned", null));
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FailureEntered.TrySetResult(); await ReleaseFailure.Task.WaitAsync(token);
                yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "Owned observed rate limit", retryAt);
            }
            else
            {
                SuccessEntered.TrySetResult(); await ReleaseSuccess.Task.WaitAsync(token);
                yield return NativeChatEvent.Delta("owned success");
            }
        }
    }
}
