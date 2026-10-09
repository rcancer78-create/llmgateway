using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class PublicTokenSettingsReviewTests
{
    [Theory]
    [InlineData("MAX_THINKING_TOKENS")]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS")]
    public async Task DocumentedBudgetsSurviveAddUpdateAndDurableReload(string name)
    {
        var folder = OwnedFolder();
        try
        {
            var options = Options(folder);
            var store = JsonAccountStore.Load(options, []);
            var draft = Profile(name, "4096");
            await store.AddAsync(draft);
            var updated = store.Find(draft.Id)!.Freeze();
            updated.Environment[name] = "8192";
            await store.UpdateAsync(updated);
            var before = File.ReadAllBytes(options.AccountsFile!);
            var reloaded = JsonAccountStore.Load(options, []);
            Assert.Equal("8192", reloaded.Find(draft.Id)!.Environment[name]);
            Assert.Equal(before, File.ReadAllBytes(options.AccountsFile!));
            Assert.Equal("4096", draft.Environment[name]);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("MAX_THINKING_TOKENS")]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS")]
    public void ExistingBudgetConfigurationIsNotSanitizedAway(string name)
    {
        var folder = OwnedFolder();
        try
        {
            var options = Options(folder);
            var initial = JsonAccountStore.Load(options, []);
            var profiles = initial.GetAll().Select(p => p.Freeze()).Append(Profile(name, "4096")).ToArray();
            File.WriteAllBytes(options.AccountsFile!, JsonSerializer.SerializeToUtf8Bytes(new { version = 2, accounts = profiles }, GatewayJson.Options));
            var loaded = JsonAccountStore.Load(options, []);
            Assert.True(loaded.Find("owned-budget")!.Environment.TryGetValue(name, out var value));
            Assert.Equal("4096", value);
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(options.AccountsFile!));
            var account = persisted.RootElement.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "owned-budget");
            Assert.Equal("4096", account.GetProperty("environment").GetProperty(name).GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("MAX_THINKING_TOKENS", "Bearer owned-not-a-real-credential")]
    [InlineData("MAX_THINKING_TOKENS", "Basic owned-not-a-real-credential")]
    [InlineData("MAX_THINKING_TOKENS", "sk-owned-not-a-real-credential")]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS", "Bearer owned-not-a-real-credential")]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS", "Basic owned-not-a-real-credential")]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS", "sk-owned-not-a-real-credential")]
    public async Task PublicBudgetNameNeverAllowsSecretShapedValue(string name, string value)
    {
        var folder = OwnedFolder();
        try
        {
            var path = Path.Combine(folder, "accounts.json");
            var store = JsonAccountStore.InMemory([], path);
            var error = await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(Profile(name, value)));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
            Assert.Null(store.Find("owned-budget"));
            Assert.False(File.Exists(path));
            Assert.DoesNotContain(value, error.Message);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("MY_SECRET_TOKENS")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    public async Task CredentialAndUnrecognizedTokenNamesRemainRejected(string name)
    {
        var store = JsonAccountStore.InMemory([]);
        var error = await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(Profile(name, "opaque-owned-placeholder")));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.Null(store.Find("owned-budget"));
    }

    private static AccountProfile Profile(string name, string value) => new()
    {
        Id = "owned-budget", DisplayName = "Owned budget", Provider = ProviderKind.Claude,
        Environment = new(StringComparer.OrdinalIgnoreCase) { [name] = value }
    };
    private static GatewayOptions Options(string folder) => new() { AccountsFile = Path.Combine(folder, "accounts.json"), DiscoverProfiles = false };
    private static string OwnedFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "llmgateway-public-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
}
