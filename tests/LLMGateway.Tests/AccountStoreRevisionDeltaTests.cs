using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class AccountStoreRevisionDeltaTests
{
    [Fact]
    public async Task StaleActualStoreCannotOverwriteAnotherStoresCommittedAccount()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-store-revision-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var options = new GatewayOptions { AccountsFile = file, DiscoverProfiles = false };
            var first = JsonAccountStore.Load(options, []);
            var stale = JsonAccountStore.Load(options, []);
            await first.AddAsync(new() { Id = "owned-first", Provider = ProviderKind.Codex });
            var committed = await File.ReadAllTextAsync(file);
            var failure = await Record.ExceptionAsync(() => stale.AddAsync(new() { Id = "owned-stale", Provider = ProviderKind.Grok }));
            Assert.IsType<GatewayException>(failure);
            Assert.Equal(committed, await File.ReadAllTextAsync(file));
            Assert.Null(stale.Find("owned-stale"));
            var fresh = JsonAccountStore.Load(options, []);
            Assert.NotNull(fresh.Find("owned-first"));
            Assert.Null(fresh.Find("owned-stale"));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ReloadedActualStoreCanCommitAndPreservesThePreviousWritersAccount()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-store-revision-control-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var options = new GatewayOptions { AccountsFile = file, DiscoverProfiles = false };
            var first = JsonAccountStore.Load(options, []);
            await first.AddAsync(new() { Id = "owned-first", Provider = ProviderKind.Codex });
            var second = JsonAccountStore.Load(options, []);
            await second.AddAsync(new() { Id = "owned-second", Provider = ProviderKind.Grok });
            var fresh = JsonAccountStore.Load(options, []);
            Assert.NotNull(fresh.Find("owned-first"));
            Assert.NotNull(fresh.Find("owned-second"));
        }
        finally { root.Delete(true); }
    }
}
