using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ChatAccountGenerationReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnAlreadyAdmittedOldProfileChatCannotPublishObservationsIntoItsReplacement(bool nativeFailure, bool removeAndReadd)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-chat-generation-");
        var adapter = new HeldAdapter(nativeFailure);
        Task<ChatResult>? admitted = null;
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory([Profile("Owned old profile")]),
                [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            admitted = gateway.CompleteAsync(new ChatRequest
            { Model = "codex/owned/model", Messages = [ChatMessage.User("Owned admitted old profile request")] });
            try
            {
                await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("Owned old profile", Assert.Single(adapter.Profiles).DisplayName);
                if (removeAndReadd)
                {
                    await gateway.RemoveAccountAsync("owned");
                    await gateway.AddAccountAsync(Profile("Owned replacement profile"));
                }
                else await gateway.UpdateAccountAsync(Profile("Owned replacement profile"));
                Assert.Equal("Owned replacement profile", Assert.Single(await gateway.GetAccountsAsync()).DisplayName);
                Assert.Equal(AccountAvailability.Unknown, Assert.Single(await gateway.GetAccountsAsync()).Availability);

                adapter.Release.TrySetResult();
                if (nativeFailure)
                {
                    var failure = await Assert.ThrowsAsync<GatewayException>(() => admitted.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(GatewayErrorKind.RateLimited, failure.Kind);
                    Assert.Equal(adapter.RetryAt, failure.RetryAt);
                }
                else Assert.Equal("owned old request answer", (await admitted.WaitAsync(TimeSpan.FromSeconds(5))).Content);

                var current = Assert.Single(await gateway.GetAccountsAsync());
                Assert.Equal("Owned replacement profile", current.DisplayName);
                Assert.Equal(AccountAvailability.Unknown, current.Availability);
                var quota = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));
                Assert.Equal(AccountAvailability.Ready, quota.Availability);
                Assert.DoesNotContain(quota.Buckets, bucket => bucket.Name == "observed");
                Assert.Single(adapter.Profiles); // Replacement never retargets/replays the admitted native request.
                Assert.Equal("Owned old profile", adapter.Profiles[0].DisplayName);
            }
            finally
            {
                adapter.Release.TrySetResult();
                await Record.ExceptionAsync(() => admitted.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally { root.Delete(true); }
    }

    private static AccountProfile Profile(string displayName) => new()
    { Id = "owned", Provider = ProviderKind.Codex, DisplayName = displayName, IsActive = true };

    private sealed class HeldAdapter(bool fail) : IProviderAdapter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AccountProfile> Profiles { get; } = [];
        public DateTimeOffset RetryAt { get; } = DateTimeOffset.UtcNow.AddMinutes(30);
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
            Profiles.Add(profile.Clone()); Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (fail) yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "Owned old profile limit", RetryAt);
            else yield return NativeChatEvent.Delta("owned old request answer");
        }
    }
}
