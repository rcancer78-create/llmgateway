using System.Reflection;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class CredentialBoundaryReviewTests
{
    [Theory]
    [InlineData("ANTHROPIC_AUTH_TOKEN", false)]
    [InlineData("ANTHROPIC_AUTH_TOKEN", true)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", false)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", true)]
    [InlineData("GOOGLE_API_KEY", false)]
    [InlineData("GOOGLE_API_KEY", true)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", false)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", true)]
    [InlineData("AZURE_OPENAI_API_KEY", false)]
    [InlineData("AZURE_OPENAI_API_KEY", true)]
    [InlineData("GH_TOKEN", false)]
    [InlineData("GH_TOKEN", true)]
    [InlineData("GITHUB_TOKEN", false)]
    [InlineData("GITHUB_TOKEN", true)]
    public void NativeLoginAndInteractiveLoginNullDocumentedAuthenticationOverrides(string name, bool login)
    {
        var delta = Build(new() { Id="owned", DisplayName="Owned", Provider=ProviderKind.Claude }, login);
        Assert.True(delta.ContainsKey(name));
        Assert.Null(delta[name]);
    }

    [Theory]
    [InlineData("--api-key", "opaque-owned-placeholder")]
    [InlineData("--api-key=opaque-owned-placeholder", "")]
    [InlineData("--token=opaque-owned-placeholder", "")]
    [InlineData("--note", "sk-owned-not-a-real-credential")]
    public async Task CredentialArgumentsAreRejectedBeforeDurableAccountPublication(string first, string second)
    {
        var path=OwnedPath();
        try
        {
            var store=JsonAccountStore.InMemory([], path);
            var draft=Profile(); draft.ExtraArguments=second.Length==0 ? [first] : [first, second];
            var error=await Assert.ThrowsAsync<GatewayException>(()=>store.AddAsync(draft));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
            Assert.DoesNotContain("placeholder", error.Message);
            Assert.DoesNotContain("sk-owned", error.Message);
            Assert.Null(store.Find(draft.Id));
            Assert.False(File.Exists(path));
            Assert.Equal(first, draft.ExtraArguments[0]);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("GITHUBTOKEN")]
    [InlineData("MYAPIKEY")]
    [InlineData("openaiApiKey")]
    public async Task OpaqueCredentialsUnderConcreteConcatenatedNamesCannotBeStored(string name)
    {
        var store=JsonAccountStore.InMemory([]);
        var draft=Profile(); draft.Environment[name]="opaque-owned-placeholder";
        var error=await Assert.ThrowsAsync<GatewayException>(()=>store.AddAsync(draft));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.Null(store.Find(draft.Id));
        Assert.DoesNotContain("opaque-owned-placeholder", error.Message);
        Assert.Single(draft.Environment);
    }

    [Fact]
    public void CredentialArgumentsInExistingConfigurationAreRefusedWithoutRewritingOriginal()
    {
        var path=OwnedPath();
        try
        {
            var account=Profile(); account.ExtraArguments=["--api-key", "opaque-owned-placeholder"];
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new { version=2, accounts=new[]{account} }, GatewayJson.Options);
            File.WriteAllBytes(path, bytes);
            var error=Assert.Throws<GatewayException>(()=>JsonAccountStore.Load(new GatewayOptions { AccountsFile=path, DiscoverProfiles=false }, []));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.DoesNotContain("placeholder", error.Message);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task OrdinaryTokenSettingsAndPublicCliFlagsStillRoundTrip()
    {
        var store=JsonAccountStore.InMemory([]);var draft=Profile();
        draft.Environment=new() { ["tokenBudget"]="1024", ["maxTokens"]="2048", ["TOKENIZERS_PARALLELISM"]="false", ["AUTH_MODE"]="ordinary-setting" };
        draft.ExtraArguments=["--verbose", "--max-turns", "1"];
        await store.AddAsync(draft);
        var saved=store.Find(draft.Id)!;
        Assert.Equal(draft.ExtraArguments, saved.ExtraArguments);
        Assert.Equal(4, saved.Environment.Count);
        Assert.Equal("1024", saved.Environment["tokenBudget"]);
    }

    [Fact]
    public void SelectedApiSourceStillMapsToItsTargetAndInteractiveLoginDoesNotForwardIt()
    {
        var source="OWNED_CREDENTIAL_SOURCE_"+Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(source,"owned-synthetic-selected");
        try
        {
            var profile=Profile();profile.AuthMode=AccountAuthMode.ApiKeyFromEnvironment;profile.ApiKeyVariable=source;
            var chat=Build(profile, false);var login=Build(profile, true);
            Assert.Equal("owned-synthetic-selected", chat["ANTHROPIC_API_KEY"]);
            Assert.Null(chat["CLAUDE_CODE_OAUTH_TOKEN"]);
            Assert.Null(login["ANTHROPIC_API_KEY"]);
        }
        finally { Environment.SetEnvironmentVariable(source,null); }
    }

    private static IReadOnlyDictionary<string,string?> Build(AccountProfile profile, bool login)
    {
        var adapter=new ClaudeAdapter(new ExecutableResolver(),new GatewayOptions(),NullLogger<ClaudeAdapter>.Instance);
        return (IReadOnlyDictionary<string,string?>)typeof(NativeAdapterBase).GetMethod("BuildEnvironment",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(adapter,[profile, login])!;
    }
    private static AccountProfile Profile()=>new(){Id="owned-credential-boundary",DisplayName="Owned",Provider=ProviderKind.Claude};
    private static string OwnedPath()
    {
        var directory=Path.Combine(Path.GetTempPath(),"credential-boundary-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);return Path.Combine(directory,"accounts.json");
    }
    private static void Cleanup(string path){var directory=Path.GetDirectoryName(path)!;if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
