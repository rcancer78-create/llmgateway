using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class InMemoryAccountAdmissionReviewTests
{
    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("remote-provider")]
    [InlineData("unknown-auth")]
    [InlineData("invalid-id")]
    [InlineData("missing-key-name")]
    [InlineData("invalid-key-name")]
    [InlineData("secret-name")]
    [InlineData("secret-value")]
    [InlineData("unsafe-argument")]
    [InlineData("newline-argument")]
    [InlineData("null-row")]
    [InlineData("null-input")]
    public void HostProvidedProfilesCannotBypassAccountAdmission(string invalid)
    {
        var root=Directory.CreateTempSubdirectory("owned-memory-admission-");
        try
        {
            var profile=Profile("owned");
            switch(invalid)
            {
                case "unknown-provider":profile.Provider=(ProviderKind)999;break;
                case "remote-provider":profile.Provider=ProviderKind.Unknown;break;
                case "unknown-auth":profile.AuthMode=(AccountAuthMode)999;break;
                case "invalid-id":profile.Id="../owned";break;
                case "missing-key-name":profile.AuthMode=AccountAuthMode.ApiKeyFromEnvironment;break;
                case "invalid-key-name":profile.ApiKeyVariable="OWNED=INJECT";break;
                case "secret-name":profile.Environment["OWNED_API_KEY"]="owned synthetic credential";break;
                case "secret-value":profile.Environment["NOTE"]="sk-owned-synthetic-not-a-real-key";break;
                case "unsafe-argument":profile.ExtraArguments=["--yolo"];break;
                case "newline-argument":profile.ExtraArguments=["owned\n--injected"];break;
            }
            IEnumerable<AccountProfile> accounts=invalid=="null-input" ? null! : invalid=="null-row" ? [null!] : [profile];
            var error=Assert.Throws<GatewayException>(()=>JsonAccountStore.InMemory(accounts,Path.Combine(root.FullName,"accounts.json")));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);
            Assert.Empty(root.GetFiles());
        }
        finally { root.Delete(true); }
    }
    [Fact]
    public void HostSnapshotCannotExceedProviderCapacity()
    {
        var root=Directory.CreateTempSubdirectory("owned-memory-capacity-");
        try
        {
            var accounts=Enumerable.Range(0,21).Select(i=>Profile("owned-"+i));
            var error=Assert.Throws<GatewayException>(()=>JsonAccountStore.InMemory(accounts,Path.Combine(root.FullName,"accounts.json")));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);Assert.Empty(root.GetFiles());
        }
        finally { root.Delete(true); }
    }
    [Fact]
    public void ValidHostApiKeyReferenceAndSettingsRemainIsolatedWithoutInitialPersistence()
    {
        var root=Directory.CreateTempSubdirectory("owned-memory-valid-");
        try
        {
            var profiles=Enumerable.Range(0,20).Select(i=>Profile("owned-"+i)).ToArray();
            profiles[0].AuthMode=AccountAuthMode.ApiKeyFromEnvironment;profiles[0].ApiKeyVariable="OWNED_MODEL_KEY";
            profiles[0].Environment["TOKENIZER_PATH"]="owned-tokenizer";
            var store=JsonAccountStore.InMemory(profiles,Path.Combine(root.FullName,"accounts.json"));
            profiles[0].Environment["TOKENIZER_PATH"]="changed";
            var copy=store.Find("owned-0")!;Assert.Equal("owned-tokenizer",copy.Environment["TOKENIZER_PATH"]);
            Assert.Equal(AccountAuthMode.ApiKeyFromEnvironment,copy.AuthMode);Assert.Equal("OWNED_MODEL_KEY",copy.ApiKeyVariable);
            copy.Environment.Clear();Assert.Single(store.Find("owned-0")!.Environment);
            Assert.Single(store.GetAll(),p=>p.IsActive);Assert.Equal(20,store.GetAll().Count);Assert.Empty(root.GetFiles());
        }
        finally { root.Delete(true); }
    }
    private static AccountProfile Profile(string id)=>new() { Id=id,Provider=ProviderKind.Codex,DisplayName=id };
}
