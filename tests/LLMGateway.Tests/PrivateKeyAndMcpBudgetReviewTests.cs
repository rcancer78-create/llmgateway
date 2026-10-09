using System.Text.Json;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class PrivateKeyAndMcpBudgetReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRejectsPgpOrJsonEmbeddedPrivateKeyWithoutChangingFile(bool json)
    {
        using var f = new Fixture();
        var before = File.ReadAllBytes(f.Options.AccountsFile!);
        await Assert.ThrowsAsync<GatewayException>(() => f.Store.AddAsync(Profile("GCP_SA_JSON", PrivateValue(json))));
        Assert.Equal(before, File.ReadAllBytes(f.Options.AccountsFile!));
        Assert.Null(f.Store.Find("owned-marker"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRejectsPrivateKeyAndPreservesExistingSettings(bool json)
    {
        using var f = new Fixture();
        await f.Store.AddAsync(Profile("GCP_SA_JSON", "owned ordinary setting"));
        var before = File.ReadAllBytes(f.Options.AccountsFile!);
        await Assert.ThrowsAsync<GatewayException>(() => f.Store.UpdateAsync(Profile("GCP_SA_JSON", PrivateValue(json))));
        Assert.Equal(before, File.ReadAllBytes(f.Options.AccountsFile!));
        Assert.Equal("owned ordinary setting", f.Reload().Find("owned-marker")!.Environment["GCP_SA_JSON"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyLoadRemovesPrivateKeyAndPreservesOrdinarySetting(bool json)
    {
        using var f = new Fixture();
        var profile = Profile("GCP_SA_JSON", PrivateValue(json));
        profile.Environment["OUTPUT_STYLE"] = "ordinary";
        f.WriteLegacy(profile);
        var loaded = f.Reload().Find(profile.Id)!;
        Assert.False(loaded.Environment.ContainsKey("GCP_SA_JSON"));
        Assert.Equal("ordinary", loaded.Environment["OUTPUT_STYLE"]);
        Assert.False(f.Reload().Find(profile.Id)!.Environment.ContainsKey("GCP_SA_JSON"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("load")]
    public async Task DocumentedMcpBudgetSurvivesDurableOperations(string operation)
    {
        using var f = new Fixture();
        var profile = Profile("MAX_MCP_OUTPUT_TOKENS", "25000");
        if (operation == "load") f.WriteLegacy(profile);
        else if (operation == "update") { await f.Store.AddAsync(Profile("OUTPUT_STYLE", "ordinary")); await f.Store.UpdateAsync(profile); }
        else await f.Store.AddAsync(profile);
        Assert.Equal("25000", f.Reload().Find(profile.Id)!.Environment["MAX_MCP_OUTPUT_TOKENS"]);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("load")]
    public async Task PublicBudgetNameStillRejectsSecretValues(string operation)
    {
        using var f = new Fixture();
        var profile = Profile("MAX_MCP_OUTPUT_TOKENS", "Bearer owned synthetic credential");
        if (operation == "load")
        {
            f.WriteLegacy(profile);
            Assert.False(f.Reload().Find(profile.Id)!.Environment.ContainsKey("MAX_MCP_OUTPUT_TOKENS"));
        }
        else if (operation == "update")
        {
            await f.Store.AddAsync(Profile("OUTPUT_STYLE", "ordinary"));
            await Assert.ThrowsAsync<GatewayException>(() => f.Store.UpdateAsync(profile));
            Assert.Equal("ordinary", f.Reload().Find(profile.Id)!.Environment["OUTPUT_STYLE"]);
        }
        else await Assert.ThrowsAsync<GatewayException>(() => f.Store.AddAsync(profile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicKeyPemAndJsonRemainOrdinarySettings(bool json)
    {
        using var f = new Fixture();
        var value = "-----BEGIN PUBLIC KEY-----\n" + Body + "\n-----END PUBLIC KEY-----";
        if (json) value = JsonSerializer.Serialize(new { public_key = value });
        await f.Store.AddAsync(Profile("PUBLIC_KEY_TEXT", value));
        Assert.Equal(value, f.Reload().Find("owned-marker")!.Environment["PUBLIC_KEY_TEXT"]);
    }

    [Fact]
    public async Task OrdinaryJsonRemainsDurable()
    {
        using var f = new Fixture();
        var value = JsonSerializer.Serialize(new { mode = "ordinary", count = 17 });
        await f.Store.AddAsync(Profile("PUBLIC_JSON", value));
        Assert.Equal(value, f.Reload().Find("owned-marker")!.Environment["PUBLIC_JSON"]);
    }

    [Fact]
    public async Task ExistingCanonicalPrivatePemRejectionRemains()
    {
        using var f = new Fixture();
        var profile = Profile("SETTING_TEXT", "-----BEGIN PRIVATE KEY-----\n" + Body + "\n-----END PRIVATE KEY-----");
        await Assert.ThrowsAsync<GatewayException>(() => f.Store.AddAsync(profile));
        f.WriteLegacy(profile);
        Assert.False(f.Reload().Find(profile.Id)!.Environment.ContainsKey("SETTING_TEXT"));
    }

    private static string Body => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("owned fixture only"));
    private static string PrivateValue(bool json) => json
        ? JsonSerializer.Serialize(new { type = "service_account", private_key = "-----BEGIN PRIVATE KEY-----\n" + Body + "\n-----END PRIVATE KEY-----" })
        : "-----BEGIN PGP PRIVATE KEY BLOCK-----\n" + Body + "\n-----END PGP PRIVATE KEY BLOCK-----";
    private static AccountProfile Profile(string name, string value) => new() { Id = "owned-marker", Provider = ProviderKind.Codex, Environment = new() { [name] = value } };
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "private-marker-review-" + Guid.NewGuid().ToString("N"));
        public GatewayOptions Options { get; }
        public JsonAccountStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            Options = new GatewayOptions { AccountsFile = Path.Combine(root, "accounts.json"), WorkspaceDirectory = root, DiscoverProfiles = false };
            Store = JsonAccountStore.Load(Options, []);
        }
        public JsonAccountStore Reload() => JsonAccountStore.Load(Options, []);
        public void WriteLegacy(AccountProfile profile) => File.WriteAllText(Options.AccountsFile!, JsonSerializer.Serialize(new { version = 2, accounts = new[] { profile } }, GatewayJson.Options));
        public void Dispose() => Directory.Delete(root, true);
    }
}
