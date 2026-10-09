using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class SdkAccountLoadReviewRegressionTests
{
    [Theory]
    [InlineData("environment")]
    [InlineData("extra_arguments")]
    public async Task NullPersistedCollectionsAreNormalizedWithoutLosingTheAccount(string collection)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-null-load-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, "{\"version\":2,\"accounts\":[{\"id\":\"owned\",\"display_name\":\"Owned\",\"provider\":\"codex\",\"" + collection + "\":null}]}");
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            var account = store.Find("owned");
            Assert.NotNull(account);
            Assert.NotNull(account.Environment);
            Assert.NotNull(account.ExtraArguments);
            Assert.Empty(account.Environment);
            Assert.Empty(account.ExtraArguments);
            var reloaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.NotNull(reloaded.Find("owned"));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task FutureFormatIsRefusedWithoutRewritingTheFileOrCreatingAFalseCorruptionBackup()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-future-load-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            const string original = "{\"version\":999,\"accounts\":[{\"id\":\"owned\",\"provider\":\"codex\"}],\"future_owned_data\":{\"preserve\":true}}";
            await File.WriteAllTextAsync(file, original);
            var failure = Record.Exception(() => JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []));
            Assert.NotNull(failure);
            Assert.Equal(original, await File.ReadAllTextAsync(file));
            Assert.Equal(new[] { "accounts.json" }, root.GetFiles().Select(entry => entry.Name).ToArray());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task LegacyUndefinedNumericProviderStringIsNotRegistered()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-provider-load-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, "[{\"id\":\"invalid-owned\",\"provider\":\"999\"},{\"id\":\"valid-owned\",\"provider\":\"Codex\"}]");
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.Null(store.Find("invalid-owned"));
            Assert.True(store.IsReadOnly);
            Assert.NotNull(store.PersistenceWarning);
            Assert.Contains("invalid-owned", await File.ReadAllTextAsync(file));
            Assert.Contains("valid-owned", await File.ReadAllTextAsync(file));
            Assert.Empty(store.GetAll());
        }
        finally { root.Delete(true); }
    }
}
