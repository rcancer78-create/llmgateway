using System.Text.Json;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class AccountProviderCapacityReviewTests
{
    [Fact]
    public async Task ProviderChangeCannotBypassDestinationCapacityOrPublishPartialMutation()
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-account-cap-");
        try
        {
            var file=Path.Combine(root.FullName,"accounts.json");
            var profiles=Full().Append(Profile("other",ProviderKind.Claude)).ToArray();
            var store=JsonAccountStore.InMemory(profiles,file);
            await store.UpdateAsync(Profile("other",ProviderKind.Claude));
            var bytes=File.ReadAllBytes(file);var before=JsonSerializer.Serialize(store.GetAll(),GatewayJson.Options);
            var error=await Assert.ThrowsAsync<GatewayException>(()=>store.UpdateAsync(Profile("other",ProviderKind.Codex)));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);
            Assert.Equal(before,JsonSerializer.Serialize(store.GetAll(),GatewayJson.Options));
            Assert.Equal(bytes,File.ReadAllBytes(file));Assert.Empty(root.GetFiles("*.tmp"));
        }
        finally { root.Delete(true); }
    }
    [Fact]
    public async Task ExistingAccountCanBeUpdatedAtExactCapacity()
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-cap-update-");
        try
        {
            var store=JsonAccountStore.InMemory(Full(),Path.Combine(root.FullName,"accounts.json"));
            var profile=store.Find("owned-0")!;profile.DisplayName="Updated owned profile";
            await store.UpdateAsync(profile);
            Assert.Equal(JsonAccountStore.MaxAccountsPerProvider,store.GetAll().Count(a=>a.Provider==ProviderKind.Codex));
            Assert.Equal(profile.DisplayName,store.Find(profile.Id)!.DisplayName);
        }
        finally { root.Delete(true); }
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void LoadedAndDiscoveredProfilesCannotExceedCapacityOrRewriteOriginalFile(bool discovery,bool legacy)
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-cap-load-");
        try
        {
            var file=Path.Combine(root.FullName,"accounts.json");
            var extra=Profile("extra",ProviderKind.Codex);extra.ConfigDirectory=Path.Combine(root.FullName,"extra-config");
            var accounts=discovery ? Full().ToArray() : Full().Append(extra).ToArray();
            var original=legacy ? JsonSerializer.Serialize(accounts,GatewayJson.Options) : JsonSerializer.Serialize(new { version=2,accounts },GatewayJson.Options);File.WriteAllText(file,original);
            var error=Assert.Throws<GatewayException>(()=>JsonAccountStore.Load(new GatewayOptions { AccountsFile=file,DiscoverProfiles=discovery },discovery ? [new Discovery(extra)] : []));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);Assert.Equal(original,File.ReadAllText(file));
            Assert.Equal(new[] { "accounts.json" },root.GetFiles().Select(f=>f.Name).ToArray());
        }
        finally { root.Delete(true); }
    }
    private static IEnumerable<AccountProfile> Full() => Enumerable.Range(0,JsonAccountStore.MaxAccountsPerProvider).Select(i=>Profile("owned-"+i,ProviderKind.Codex));
    private static AccountProfile Profile(string id,ProviderKind provider) => new() { Id=id,Provider=provider,DisplayName=id };
    private sealed class Discovery(AccountProfile profile) : IProviderAdapter
    {
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned discovery";
        public string DefaultExecutable => "unused";
        public ProviderCapabilities Capabilities => new(true,"owned",MultiAccountSupport.Isolated,"owned",null,null,true,100000);
        public string? ResolveExecutable(AccountProfile account) => null;
        public IEnumerable<AccountProfile> DiscoverProfiles() => [profile];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account,CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account,CancellationToken token) => throw new NotSupportedException();
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account,CancellationToken token) => throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account,CancellationToken token) => throw new NotSupportedException();
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account,NativeChatRequest request,CancellationToken token) => throw new NotSupportedException();
    }
}
