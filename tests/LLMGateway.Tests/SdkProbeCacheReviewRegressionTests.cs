using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class SdkProbeCacheReviewRegressionTests
{
    [Theory]
    [InlineData("models", false)]
    [InlineData("quota", false)]
    [InlineData("status", false)]
    [InlineData("models", true)]
    [InlineData("quota", true)]
    [InlineData("status", true)]
    public async Task OldProbeCompletionCannotReplaceTheNewProfileGeneration(string kind, bool removeAndReadd)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-generation-");
        try
        {
            var adapter = new DeferredAdapter(kind);
            var store = JsonAccountStore.InMemory([Profile("old")], Path.Combine(root.FullName, "accounts.json"));
            using var gateway = new LlmGateway(store, [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            var oldProbe = ProbeAsync(gateway, kind);
            try
            {
                await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (removeAndReadd)
                {
                    await gateway.RemoveAccountAsync("owned");
                    await gateway.AddAccountAsync(Profile("new"));
                }
                else await gateway.UpdateAccountAsync(Profile("new"));

                // Publish a successful new-profile observation before the old response arrives.
                await ProbeAsync(gateway, kind);
                adapter.Release.TrySetResult();
                await oldProbe.WaitAsync(TimeSpan.FromSeconds(5));
                if (kind == "models")
                    Assert.Equal("new-model", Assert.Single(await gateway.GetModelsAsync(false, "owned")).NativeModel);
                else if (kind == "quota")
                    Assert.Equal("new", Assert.Single(await gateway.GetQuotasAsync(false, "owned")).Plan);
                else
                    Assert.Equal("new", Assert.Single(await gateway.GetAccountsAsync()).Identity!.Email);
                Assert.Equal(2, adapter.ProbeCalls);
            }
            finally
            {
                adapter.Release.TrySetResult();
                await Record.ExceptionAsync(() => oldProbe);
            }
        }
        finally { root.Delete(true); }
    }

    private static AccountProfile Profile(string name) => new()
    { Id = "owned", DisplayName = name, Provider = ProviderKind.Codex, IsActive = true };

    private static async Task ProbeAsync(LlmGateway gateway, string kind)
    {
        if (kind == "models") await gateway.GetModelsAsync(true, "owned");
        else if (kind == "quota") await gateway.GetQuotasAsync(true, "owned");
        else await gateway.CheckAccountAsync("owned");
    }

    private sealed class DeferredAdapter(string kind) : IProviderAdapter
    {
        private int _probeCalls;
        public int ProbeCalls => Volatile.Read(ref _probeCalls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(true, "owned fake", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "owned-fake";

        private async Task WaitAsync(string operation, CancellationToken token)
        {
            if (operation != kind) return;
            if (Interlocked.Increment(ref _probeCalls) != 1) return;
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
        }

        public async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token)
        {
            var name = account.DisplayName;
            await WaitAsync("models", token);
            return [new(name + "-model", name)];
        }
        public async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token)
        {
            var name = account.DisplayName;
            await WaitAsync("quota", token);
            return new(account.Id, Provider, DateTimeOffset.UtcNow, AccountAvailability.Ready, true, name, [], "owned", null);
        }
        public async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token)
        {
            var name = account.DisplayName;
            await WaitAsync("status", token);
            return new(AccountAvailability.Ready, new(name, name, null), "owned", null, DateTimeOffset.UtcNow);
        }
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Probe regression must never dispatch a chat.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
