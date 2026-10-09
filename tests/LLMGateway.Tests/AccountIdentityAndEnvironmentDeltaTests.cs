using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class AccountIdentityAndEnvironmentDeltaTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AmbiguousPersistedAccountIdentityIsRefusedWithoutRewriting(bool legacy, bool crossProvider)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-duplicate-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var accounts = new[] { Profile("owned", ProviderKind.Codex), Profile("OWNED", crossProvider ? ProviderKind.Grok : ProviderKind.Codex) };
            var original = legacy
                ? "[{\"id\":\"owned\",\"provider\":\"Codex\"},{\"id\":\"OWNED\",\"provider\":\"" + (crossProvider ? "Grok" : "Codex") + "\"}]"
                : JsonSerializer.Serialize(new { version = 2, accounts }, GatewayJson.Options);
            await File.WriteAllTextAsync(file, original);
            var failure = Record.Exception(() => JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []));
            Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.IsType<GatewayException>(failure).Kind);
            Assert.Equal(original, await File.ReadAllTextAsync(file));
            Assert.Equal(new[] { "accounts.json" }, root.GetFiles().Select(item => item.Name).ToArray());
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InMemoryIdentityUsesTheSameCaseInsensitiveUniqueness(bool crossProvider)
    {
        var failure = Record.Exception(() => JsonAccountStore.InMemory([Profile("owned", ProviderKind.Codex),
            Profile("OWNED", crossProvider ? ProviderKind.Grok : ProviderKind.Codex)]));
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.IsType<GatewayException>(failure).Kind);
    }

    [Theory]
    [InlineData("sk-owned-fixture")]
    [InlineData("ghp_owned_fixture")]
    [InlineData("github_pat_owned_fixture")]
    [InlineData("xai-owned-fixture")]
    [InlineData("AIzaOwnedFixture")]
    public async Task LeadingWhitespaceCannotPersistCredentialShapedSettings(string syntheticValue)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-secret-shape-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            var before = await File.ReadAllTextAsync(file);
            var profile = Profile("owned", ProviderKind.Codex);
            profile.Environment["OWNED_SETTING"] = " \t" + syntheticValue; // Fabricated shape, never a credential.
            var failure = await Record.ExceptionAsync(() => store.AddAsync(profile));
            Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.IsType<GatewayException>(failure).Kind);
            Assert.Null(store.Find("owned"));
            Assert.Equal(before, await File.ReadAllTextAsync(file));
            Assert.Equal(" \t" + syntheticValue, profile.Environment["OWNED_SETTING"]);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("sk-owned-fixture")]
    [InlineData("ghp_owned_fixture")]
    [InlineData("github_pat_owned_fixture")]
    [InlineData("xai-owned-fixture")]
    [InlineData("AIzaOwnedFixture")]
    public async Task LoadRemovesWhitespacePrefixedCredentialShapesFromItsOwnedRecoverySnapshot(string syntheticValue)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-secret-load-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var profile = Profile("owned", ProviderKind.Codex);
            profile.Environment["OWNED_SETTING"] = " \t" + syntheticValue;
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 2, accounts = new[] { profile } }, GatewayJson.Options));
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.Empty(store.Find("owned")!.Environment);
            Assert.DoesNotContain(syntheticValue, await File.ReadAllTextAsync(file));
            Assert.Empty(JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []).Find("owned")!.Environment);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task DistinctIdentitiesAndOrdinaryWhitespaceSettingRemainSupported()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-identity-control-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var store = JsonAccountStore.InMemory([Profile("owned-one", ProviderKind.Codex), Profile("owned-two", ProviderKind.Grok)], file);
            var profile = Profile("owned-three", ProviderKind.Codex);
            profile.Environment["OWNED_SETTING"] = " \tordinary-setting";
            await store.AddAsync(profile);
            var loaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.Equal(ProviderKind.Codex, loaded.Find("OWNED-ONE")!.Provider);
            Assert.Equal(ProviderKind.Grok, loaded.Find("OWNED-TWO")!.Provider);
            Assert.Equal(" \tordinary-setting", loaded.Find("owned-three")!.Environment["OWNED_SETTING"]);
        }
        finally { root.Delete(true); }
    }

    private static AccountProfile Profile(string id, ProviderKind provider) => new() { Id = id, Provider = provider, DisplayName = id };
}
