using System.Text;
using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class AccountFileShapeAndUriCredentialDeltaTests
{
    [Theory]
    [InlineData("null", GatewayErrorKind.InvalidRequest)]
    [InlineData("42", GatewayErrorKind.InvalidRequest)]
    [InlineData("true", GatewayErrorKind.InvalidRequest)]
    [InlineData("\"owned scalar\"", GatewayErrorKind.InvalidRequest)]
    [InlineData("{}", GatewayErrorKind.InvalidRequest)]
    [InlineData("{\"version\":2,\"owned_preserve\":\"owned-format-sentinel\"}", GatewayErrorKind.InvalidRequest)]
    [InlineData("{\"version\":2,\"accounts\":null,\"owned_preserve\":\"owned-format-sentinel\"}", GatewayErrorKind.InvalidRequest)]
    [InlineData("{\"version\":2,\"accounts\":{},\"owned_preserve\":\"owned-format-sentinel\"}", GatewayErrorKind.InvalidRequest)]
    [InlineData("{\"version\":0,\"accounts\":[],\"owned_preserve\":\"owned-format-sentinel\"}", GatewayErrorKind.Unsupported)]
    [InlineData("{\"version\":1,\"accounts\":[],\"owned_preserve\":\"owned-format-sentinel\"}", GatewayErrorKind.Unsupported)]
    public async Task UnsupportedAccountDocumentShapeCannotBecomeAnEmptyDefaultSnapshot(string json, GatewayErrorKind expectedKind)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-document-shape-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = Encoding.UTF8.GetBytes(json);
            await File.WriteAllBytesAsync(file, original);

            var failure = Record.Exception(() => JsonAccountStore.Load(Options(file), []));

            Assert.Equal(original, await File.ReadAllBytesAsync(file)); // No destructive rewrite, including on refusal.
            var error = Assert.IsType<GatewayException>(failure);
            Assert.Equal(expectedKind, error.Kind);
            Assert.DoesNotContain("owned-format-sentinel", error.Message);
            Assert.DoesNotContain(root.FullName, error.Message);
            Assert.Equal(new[] { "accounts.json" }, root.GetFiles().Select(item => item.Name).ToArray());
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"accounts\":[]}")]
    public async Task ExplicitSupportedEmptyAccountFormatsStillCreateUsableDefaults(string json)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-empty-format-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, json);
            var store = JsonAccountStore.Load(Options(file), []);
            Assert.NotEmpty(store.GetAll());
            Assert.NotNull(store.Find("codex-default"));
            using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            Assert.Equal(2, persisted.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(JsonValueKind.Array, persisted.RootElement.GetProperty("accounts").ValueKind);
            Assert.Equal(store.GetAll().Select(account => account.Id),
                JsonAccountStore.Load(Options(file), []).GetAll().Select(account => account.Id));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SyntacticallyCorruptAccountFileKeepsTheDocumentedRecoveryCopy()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-syntax-recovery-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = Encoding.UTF8.GetBytes("{\"owned_syntax_sentinel\":");
            await File.WriteAllBytesAsync(file, original);
            var store = JsonAccountStore.Load(Options(file), []);
            Assert.True(store.IsReadOnly);
            Assert.NotNull(store.PersistenceWarning);
            Assert.Empty(store.GetAll());
            var backup = Assert.Single(root.GetFiles("accounts.json.corrupt-*"));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup.FullName));
            Assert.Equal(original, await File.ReadAllBytesAsync(file));
            await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(new AccountProfile { Id = "owned-new", Provider = ProviderKind.Codex }));
            Assert.Equal(original, await File.ReadAllBytesAsync(file));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("http://owned-user:owned-password@proxy.invalid:8080/")]
    [InlineData("https://owned%3Auser:owned%40password@proxy.invalid/")]
    [InlineData(" \thttp://owned-user:owned-password@proxy.invalid/ ")]
    public async Task ConcreteProxyUriCredentialsCannotBePersistedAsSettings(string uri)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-proxy-add-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var store = JsonAccountStore.Load(Options(file), []);
            var original = await File.ReadAllBytesAsync(file);
            var profile = Profile(uri);

            var failure = await Record.ExceptionAsync(() => store.AddAsync(profile));

            Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.IsType<GatewayException>(failure).Kind);
            Assert.Null(store.Find(profile.Id));
            Assert.Equal(original, await File.ReadAllBytesAsync(file));
            Assert.Equal(uri, profile.Environment["HTTPS_PROXY"]);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("http://owned-user:owned-password@proxy.invalid:8080/")]
    [InlineData("https://owned%3Auser:owned%40password@proxy.invalid/")]
    [InlineData(" \thttp://owned-user:owned-password@proxy.invalid/ ")]
    public async Task LoadRemovesProxyUriCredentialsWithoutLosingTheAccount(string uri)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-proxy-load-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 2, accounts = new[] { Profile(uri) } }, GatewayJson.Options));

            var store = JsonAccountStore.Load(Options(file), []);

            Assert.Empty(store.Find("owned-proxy")!.Environment);
            using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            var account = Assert.Single(persisted.RootElement.GetProperty("accounts").EnumerateArray(),
                item => item.GetProperty("id").GetString() == "owned-proxy");
            Assert.Empty(account.GetProperty("environment").EnumerateObject());
            Assert.Empty(JsonAccountStore.Load(Options(file), []).Find("owned-proxy")!.Environment);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task OrdinaryProxyUriAndAtSignInPathAreSettingsWithoutCredentials()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-proxy-control-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var store = JsonAccountStore.Load(Options(file), []);
            var profile = Profile("https://proxy.invalid:8080/path/owned@resource");
            profile.Environment["OWNED_PATH"] = "owned@local/path";
            await store.AddAsync(profile);
            var persisted = JsonAccountStore.Load(Options(file), []).Find(profile.Id)!;
            Assert.Equal(profile.Environment["HTTPS_PROXY"], persisted.Environment["HTTPS_PROXY"]);
            Assert.Equal(profile.Environment["OWNED_PATH"], persisted.Environment["OWNED_PATH"]);
        }
        finally { root.Delete(true); }
    }

    private static GatewayOptions Options(string file) => new() { AccountsFile = file, DiscoverProfiles = false };
    private static AccountProfile Profile(string uri) => new()
    {
        Id = "owned-proxy", Provider = ProviderKind.Codex, DisplayName = "Owned",
        Environment = new(StringComparer.OrdinalIgnoreCase) { ["HTTPS_PROXY"] = uri }
    };
}
