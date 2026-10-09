using System.Text.Json;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class AccountCollectionShapeReviewTests
{
    [Theory]
    [InlineData("case-collision")]
    [InlineData("null-value")]
    [InlineData("null-argument")]
    [InlineData("nul-value")]
    [InlineData("nul-name")]
    [InlineData("equals-name")]
    [InlineData("empty-name")]
    public void CollectionShapeFailuresHaveAClassifiedErrorBeforeStoreCreation(string shape)
    {
        var profile = Profile(shape);
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => profile.Clone()).Kind);
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => profile.Freeze()).Kind);
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => JsonAccountStore.InMemory([profile])).Kind);
    }
    [Theory]
    [InlineData("case-collision")]
    [InlineData("null-value")]
    [InlineData("null-argument")]
    [InlineData("nul-value")]
    [InlineData("nul-name")]
    [InlineData("equals-name")]
    [InlineData("empty-name")]
    public void InvalidLoadedCollectionsAreRejectedWithoutRewritingTheConfiguration(string shape)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-collection-shape-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = JsonSerializer.Serialize(new { version=2, accounts=new[] { Profile(shape) } }, GatewayJson.Options);
            File.WriteAllText(file, original);
            var error = Assert.Throws<GatewayException>(() => JsonAccountStore.Load(new() { AccountsFile=file, DiscoverProfiles=false }, []));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal(new[] { "accounts.json" }, root.GetFiles().Select(f => f.Name).ToArray());
        }
        finally { root.Delete(true); }
    }
    private static AccountProfile Profile(string shape)
    {
        var result = new AccountProfile { Id="owned", DisplayName="Owned", Provider=ProviderKind.Codex };
        if (shape=="case-collision") result.Environment = new(StringComparer.Ordinal) { ["Path"]="one", ["PATH"]="two" };
        else if (shape=="null-value") result.Environment["NOTE"] = null!;
        else if (shape=="nul-value") result.Environment["NOTE"] = "prefix\0INJECTED=synthetic";
        else if (shape=="nul-name") result.Environment["BAD\0NAME"] = "synthetic";
        else if (shape=="equals-name") result.Environment["BAD=NAME"] = "synthetic";
        else if (shape=="empty-name") result.Environment[""] = "synthetic";
        else result.ExtraArguments = [null!];
        return result;
    }
}
