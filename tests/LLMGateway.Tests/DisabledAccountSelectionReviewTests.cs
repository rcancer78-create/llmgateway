using System.Text.Json;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class DisabledAccountSelectionReviewTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task SelectingDisabledAccountFailsWithoutChangingActiveAccountOrPersistedBytes(bool persisted,bool uppercaseId)
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-disabled-selection-");
        try
        {
            var file=Path.Combine(root.FullName,"accounts.json");
            var enabled=new AccountProfile { Id="owned-enabled",Provider=ProviderKind.Codex,IsActive=true,Enabled=true };
            var disabled=new AccountProfile { Id="owned-disabled",Provider=ProviderKind.Codex,Enabled=false };
            var store=persisted
                ? JsonAccountStore.Load(new GatewayOptions { AccountsFile=file,DiscoverProfiles=false },[])
                : JsonAccountStore.InMemory([enabled,disabled]);
            if(persisted)
            {
                await store.AddAsync(enabled);
                await store.AddAsync(disabled);
                await store.SelectAsync(enabled.Id);
            }
            var before=JsonSerializer.Serialize(store.GetAll(),GatewayJson.Options);
            var bytes=persisted ? File.ReadAllBytes(file) : null;
            var modified=persisted ? File.GetLastWriteTimeUtc(file) : default;
            var error=await Assert.ThrowsAsync<GatewayException>(() => store.SelectAsync(uppercaseId ? disabled.Id.ToUpperInvariant() : disabled.Id));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);
            Assert.Equal(before,JsonSerializer.Serialize(store.GetAll(),GatewayJson.Options));
            Assert.True(store.Find(enabled.Id)!.IsActive);
            Assert.False(store.Find(disabled.Id)!.IsActive);
            if(persisted)
            {
                Assert.Equal(bytes,File.ReadAllBytes(file));
                Assert.Equal(modified,File.GetLastWriteTimeUtc(file));
                Assert.Empty(root.GetFiles("*.tmp"));
            }
        }
        finally { root.Delete(true); }
    }
}
