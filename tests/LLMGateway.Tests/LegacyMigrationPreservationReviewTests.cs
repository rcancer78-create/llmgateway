using System.Text;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class LegacyMigrationPreservationReviewTests
{
    [Fact]
    public void ValidLegacyMigrationPreservesTheOriginalBytesBeforePublishingVersionTwo()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-v1-backup-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = Encoding.UTF8.GetBytes("[{\"id\":\"owned\",\"provider\":\"codex\",\"displayName\":\"Owned\"}]");
            File.WriteAllBytes(file, original);
            var store = JsonAccountStore.Load(new() { AccountsFile=file, DiscoverProfiles=false }, []);
            Assert.False(store.IsReadOnly); Assert.NotNull(store.Find("owned"));
            var backup = Assert.Single(Directory.GetFiles(root.FullName, "accounts.json.v1-*"));
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.Contains("\"version\": 2", File.ReadAllText(file));
        }
        finally { root.Delete(true); }
    }
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"id\":\"bad id\",\"provider\":\"codex\"}")]
    [InlineData("{\"id\":\"other\",\"provider\":\"unknown-provider\"}")]
    public void ARejectedLegacyEntryCannotBeSilentlyDroppedWhileTheFileIsRewritten(string entry)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-v1-invalid-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = Encoding.UTF8.GetBytes("[{\"id\":\"owned\",\"provider\":\"codex\"}," + entry + "]");
            File.WriteAllBytes(file, original);
            var store = JsonAccountStore.Load(new() { AccountsFile=file, DiscoverProfiles=false }, []);
            Assert.True(store.IsReadOnly); Assert.NotNull(store.PersistenceWarning);
            Assert.Equal(original, File.ReadAllBytes(file));
            Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(root.FullName, "accounts.json.corrupt-*"))));
        }
        finally { root.Delete(true); }
    }
}
