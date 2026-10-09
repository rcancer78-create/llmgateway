using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class InMemoryPersistenceBoundaryReviewTests
{
    [Fact]
    public async Task DefaultMemoryStoreMutationsDoNotCreateAccountOrLeaseFiles()
    {
        var store = JsonAccountStore.InMemory([Profile("owned-seed")]);
        try
        {
            await store.AddAsync(Profile("owned-new"));
            AssertNoFiles(store);
            await store.SelectAsync("owned-new");
            AssertNoFiles(store);
            var update = store.Find("owned-new")!;
            update.DisplayName = "Owned changed name";
            await store.UpdateAsync(update);
            AssertNoFiles(store);
            await store.RemoveAsync("owned-seed");
            AssertNoFiles(store);
            Assert.Equal("Owned changed name", Assert.Single(store.GetAll()).DisplayName);
            Assert.True(Assert.Single(store.GetAll()).IsActive);
            Assert.Equal(5, store.RevisionId);
        }
        finally { File.Delete(store.FilePath); File.Delete(store.FilePath + ".lock"); }
    }

    [Fact]
    public async Task ExplicitBackingFileRetainsTheOptInPersistenceContract()
    {
        using var root = new TestDirectory();
        var file = root.GetPath("accounts.json");
        var store = JsonAccountStore.InMemory([Profile("owned-seed")], file);
        Assert.False(File.Exists(file));
        await store.AddAsync(Profile("owned-new"));
        Assert.True(File.Exists(file));
        var loaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
        Assert.NotNull(loaded.Find("owned-new"));
    }

    private static AccountProfile Profile(string id) => new() { Id = id, Provider = ProviderKind.Codex, DisplayName = id, Enabled = true };
    private static void AssertNoFiles(JsonAccountStore store)
    {
        Assert.False(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".lock"));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, Path.GetFileName(store.FilePath) + ".*.tmp"));
    }
}
