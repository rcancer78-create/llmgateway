using System.Text.Json;
using LLMGateway.Core;
using Xunit;

namespace LLMGateway.Tests;

public sealed class CredentialFormatBoundaryReviewTests
{
    [Theory]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl ")]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl\t")]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl\r\n")]
    [InlineData("gho_owned_synthetic_not_a_credential")]
    [InlineData("ghu_owned_synthetic_not_a_credential")]
    [InlineData("ghs_owned_synthetic_not_a_credential")]
    [InlineData("ghr_owned_synthetic_not_a_credential")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nowned-synthetic-placeholder\n-----END EC PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nowned-synthetic-placeholder\n-----END OPENSSH PRIVATE KEY-----")]
    public async Task CredentialValueIsRefusedBeforeChangingExistingStore(string value)
    {
        using var owned = new OwnedFixture();
        var store = JsonAccountStore.InMemory([], owned.Path);
        await store.AddAsync(Profile("seed", "ordinary notes"));
        var before = File.ReadAllBytes(owned.Path);
        var candidate = Profile("candidate", value);
        var error = await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(candidate));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(before, File.ReadAllBytes(owned.Path));
        Assert.Null(store.Find(candidate.Id));
        Assert.Equal(value, candidate.Environment["NOTES"]);
        Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl ")]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl\t")]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl\r\n")]
    [InlineData("gho_owned_synthetic_not_a_credential")]
    [InlineData("ghu_owned_synthetic_not_a_credential")]
    [InlineData("ghs_owned_synthetic_not_a_credential")]
    [InlineData("ghr_owned_synthetic_not_a_credential")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nowned-synthetic-placeholder\n-----END EC PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nowned-synthetic-placeholder\n-----END OPENSSH PRIVATE KEY-----")]
    public void ExistingCredentialValueIsRemovedBeforeLoadAndOutboundPublication(string value)
    {
        using var owned = new OwnedFixture();
        var candidate = Profile("existing", value);
        File.WriteAllBytes(owned.Path, JsonSerializer.SerializeToUtf8Bytes(
            new { version = 2, accounts = new[] { candidate } }, GatewayJson.Options));
        var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = owned.Path, DiscoverProfiles = false }, []);
        Assert.False(store.Find(candidate.Id)!.Environment.ContainsKey("NOTES"));
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(owned.Path));
        var durableAccount = persisted.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(account => account.GetProperty("id").GetString() == candidate.Id);
        Assert.False(durableAccount.GetProperty("environment").TryGetProperty("NOTES", out _));
        Assert.False(AccountEnvironment.WithoutSecrets(candidate.Environment).ContainsKey("NOTES"));
        Assert.Equal(value, candidate.Environment["NOTES"]);
    }

    [Fact]
    public async Task OrdinaryGitHubLikeNotesStillPersistAndLoad()
    {
        using var owned = new OwnedFixture();
        const string value = "ghost_notes and gho ordinary setting";
        var store = JsonAccountStore.InMemory([], owned.Path);
        await store.AddAsync(Profile("ordinary", value));
        var reloaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = owned.Path, DiscoverProfiles = false }, []);
        Assert.Equal(value, reloaded.Find("ordinary")!.Environment["NOTES"]);
        Assert.Equal(value, AccountEnvironment.WithoutSecrets(reloaded.Find("ordinary")!.Environment)["NOTES"]);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public async Task WhitespaceSuffixedJwtCannotReplaceADurablePublicSetting(string suffix)
    {
        // Explicitly owned JWT-shaped fixture, with no real issuer or valid signature.
        const string ownedJwt = "eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.b3duZWRub3Rhc2lnbmF0dXJl";
        using var owned = new OwnedFixture();
        var store = JsonAccountStore.InMemory([], owned.Path);
        await store.AddAsync(Profile("existing", "ordinary public setting"));
        var before = File.ReadAllBytes(owned.Path);
        var candidate = Profile("existing", ownedJwt + suffix);
        Assert.True(AccountEnvironment.IsSecret("NOTES", ownedJwt));
        Assert.True(AccountEnvironment.IsSecret("NOTES", candidate.Environment["NOTES"]));
        var error = await Assert.ThrowsAsync<GatewayException>(() => store.UpdateAsync(candidate));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(before, File.ReadAllBytes(owned.Path));
        Assert.Equal("ordinary public setting", store.Find("existing")!.Environment["NOTES"]);
        Assert.DoesNotContain(ownedJwt, error.Message);
    }

    [Fact]
    public async Task WhitespaceInAnOrdinaryPublicSettingIsPreserved()
    {
        using var owned = new OwnedFixture();
        const string value = " ordinary public setting \t\r\n";
        Assert.False(AccountEnvironment.IsSecret("NOTES", value));
        var store = JsonAccountStore.InMemory([], owned.Path);
        await store.AddAsync(Profile("ordinary", value));
        await store.UpdateAsync(Profile("ordinary", value));
        var reloaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = owned.Path, DiscoverProfiles = false }, []);
        Assert.Equal(value, reloaded.Find("ordinary")!.Environment["NOTES"]);
        Assert.Equal(value, AccountEnvironment.WithoutSecrets(reloaded.Find("ordinary")!.Environment)["NOTES"]);
    }

    private static AccountProfile Profile(string id, string value) => new()
    {
        Id = id, DisplayName = "Owned synthetic format fixture", Provider = ProviderKind.Claude,
        Environment = new() { ["NOTES"] = value }
    };

    private sealed class OwnedFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "owned-secret-format-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "accounts.json");
        public OwnedFixture() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
