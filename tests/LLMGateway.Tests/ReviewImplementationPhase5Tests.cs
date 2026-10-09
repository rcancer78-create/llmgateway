using System.Reflection;
using LLMGateway.Core;
using LLMGateway.Native;

namespace LLMGateway.Tests;

public sealed class ReviewImplementationPhase5Tests
{
    [Fact]
    public void StopSequenceFilter_HandlesSurrogatePairs_AndSplitDeltas()
    {
        var filter = new StopSequenceFilter(["🛑stop"]);
        // "Header text 😊 🛑" (17 chars). Stop sequence "🛑stop" is 6 chars, so holdBack = 5.
        // First push emits safe prefix "Header text ", holding back "😊 🛑" in pending buffer.
        var update1 = filter.Push("Header text 😊 🛑");
        // Second push appends "stop after this". Match "🛑stop" found! Emits pending prefix "😊 ".
        var update2 = filter.Push("stop after this");

        Assert.Equal("Header text ", update1);
        Assert.Equal("😊 ", update2);
        Assert.True(filter.Stopped);
        Assert.Equal("Header text 😊 ", update1 + update2);
    }

    [Fact]
    public void OutputBudget_Fit_PreservesSurrogatePairs()
    {
        // Emoji 😊 is at index 6,7 (surrogate pair).
        // With maxTokens=2 and alreadyChars=1, allowed chars = 7 (which cuts right between surrogate pair).
        // OutputBudget.Fit detects surrogate split at index 7 and reduces allowed to 6 ("Hello ").
        var fitted = OutputBudget.Fit("Hello 😊 World", 1, 2, out var exhausted);
        Assert.True(exhausted);
        Assert.Equal("Hello ", fitted);
    }

    [Fact]
    public void CliArguments_Parse_UnclosedQuote_ThrowsInvalid()
    {
        var failure = Assert.Throws<GatewayException>(() => CliArguments.Parse("arg1 \"unclosed quote"));
        Assert.Equal(GatewayErrorKind.InvalidRequest, failure.Kind);
        Assert.Contains("кавычка", failure.Message);
    }

    [Fact]
    public void CliArguments_RejectUnsafe_RejectsDangerousPrefixes()
    {
        var failure1 = Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(["--dangerously-auto-approve"]));
        Assert.Equal(GatewayErrorKind.InvalidRequest, failure1.Kind);

        var failure2 = Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(["--allow-dangerously-exec"]));
        Assert.Equal(GatewayErrorKind.InvalidRequest, failure2.Kind);

        var failure3 = Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(["--yolo"]));
        Assert.Equal(GatewayErrorKind.InvalidRequest, failure3.Kind);
    }

    [Fact]
    public void JsonAccountStore_CorruptFile_CreatesBackupCopy()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-corrupt-test-");
        try
        {
            var options = new GatewayOptions { AccountsFile = Path.Combine(root.FullName, "accounts.json") };
            var file = options.ResolveAccountsFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ invalid json content... ");
            _ = JsonAccountStore.Load(options, []);
            var corruptFiles = Directory.GetFiles(root.FullName, "*.corrupt-*", SearchOption.AllDirectories);
            Assert.Single(corruptFiles);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AccountProfile_Clone_CopiesAllProperties()
    {
        var original = new AccountProfile
        {
            Id = "test-id",
            DisplayName = "Test Account",
            Provider = ProviderKind.Antigravity,
            Executable = "agy.exe",
            ConfigDirectory = "C:\\Config",
            AuthMode = AccountAuthMode.ApiKeyFromEnvironment,
            ApiKeyVariable = "MY_KEY",
            WorkingDirectory = "C:\\Work",
            DefaultModel = "gemini-pro",
            IsActive = true,
            Enabled = false,
            ExtraArguments = ["--arg1", "--arg2"]
        };
        original.Environment["ENV_1"] = "VAL_1";

        var cloned = original.Clone();

        foreach (var prop in typeof(AccountProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(Dictionary<string, string>))
            {
                var origDict = (Dictionary<string, string>)prop.GetValue(original)!;
                var cloneDict = (Dictionary<string, string>)prop.GetValue(cloned)!;
                Assert.Equal(origDict, cloneDict);
                Assert.NotSame(origDict, cloneDict);
            }
            else if (prop.PropertyType == typeof(List<string>))
            {
                var origList = (List<string>)prop.GetValue(original)!;
                var cloneList = (List<string>)prop.GetValue(cloned)!;
                Assert.Equal(origList, cloneList);
                Assert.NotSame(origList, cloneList);
            }
            else
            {
                Assert.Equal(prop.GetValue(original), prop.GetValue(cloned));
            }
        }
    }

    [Fact]
    public void ExecutableResolver_ResolvesSystemExecutable()
    {
        var resolver = new ExecutableResolver();
        var resolved = resolver.Resolve("cmd");
        Assert.NotNull(resolved);
    }
}
