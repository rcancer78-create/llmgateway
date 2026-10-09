using LLMGateway.Core;
using LLMGateway.ChatApp;
namespace LLMGateway.ChatApp.Tests;
public sealed class AccountEditorEnvironmentReviewTests
{
    [Fact]
    public void LoadingAndSavingPreservesSemicolonsEqualsAndValueWhitespace()
    {
        var expected = new Dictionary<string, string> { ["PATH"] = "C:\\one;C:\\two", ["NOTE"] = " value=with;punctuation " };
        var info = new AccountInfo("owned", "Owned", ProviderKind.Codex, true, true, AccountAvailability.Unknown,
            null, null, null, AccountAuthMode.NativeLogin, null, null, null, null, null, expected);
        var editor = new AccountEditorViewModel(); editor.Load(info);
        var actual = editor.ToProfile().Environment;
        Assert.Equal(expected.Count, actual.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
    [Theory]
    [InlineData("broken-line")]
    [InlineData("=empty-name")]
    [InlineData("NOTE=one\nNOTE=two")]
    public void MalformedOrDuplicateEntriesAreRejectedInsteadOfSilentlyDropped(string text)
    {
        var editor = new AccountEditorViewModel { Id="owned", EnvironmentText=text };
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => editor.ToProfile()).Kind);
    }
}
