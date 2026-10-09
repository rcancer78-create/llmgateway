using System.Text;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class CorruptAccountWriteRefusalReviewTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("remove")]
    [InlineData("select")]
    public async Task RecoveryNeverOverwritesTheOriginalOrPermitsSubsequentMutation(string operation)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-readonly-recovery-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = Encoding.UTF8.GetBytes("{\"owned_corrupt_record\":"); File.WriteAllBytes(file, original);
            var store = JsonAccountStore.Load(new() { AccountsFile=file, DiscoverProfiles=false }, []);
            Assert.True(store.IsReadOnly); Assert.NotNull(store.PersistenceWarning); Assert.Empty(store.GetAll());
            var profile = new AccountProfile { Id="owned", DisplayName="Owned", Provider=ProviderKind.Codex };
            var error = await Record.ExceptionAsync(() => operation switch
            {
                "add" => store.AddAsync(profile), "update" => store.UpdateAsync(profile),
                "remove" => store.RemoveAsync("owned"), _ => store.SelectAsync("owned")
            });
            Assert.IsType<GatewayException>(error);
            Assert.Equal(original, File.ReadAllBytes(file));
            Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(root.FullName, "accounts.json.corrupt-*"))));
            Assert.Empty(store.GetAll());
            Assert.False(File.Exists(file+".lock")); Assert.Empty(Directory.GetFiles(root.FullName,"*.tmp"));
        }
        finally { root.Delete(true); }
    }
}
