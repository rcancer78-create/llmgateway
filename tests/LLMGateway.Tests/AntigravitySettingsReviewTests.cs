using System.Text.Json.Nodes;
using LLMGateway.Native.Adapters;

namespace LLMGateway.Tests;

public sealed class AntigravitySettingsReviewTests
{
    [Theory]
    [InlineData("{\"unrelated\":")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    public async Task ExistingMalformedSettingsAreRefusedWithoutReplacingOriginalBytes(string original)
    {
        using var directory = new TestDirectory();
        var file = SettingsPath(directory.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, original);
        var before = await File.ReadAllBytesAsync(file);

        var failure = Record.Exception(() => AntigravityAdapter.EnsureApiKeySettings(directory.Root));

        Assert.NotNull(failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal("settings.json", Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(file)!)).Split(Path.DirectorySeparatorChar)[^1]);
    }

    [Fact]
    public async Task ConcurrentSettingsPublicationPreservesAnOpenReaderSnapshot()
    {
        using var directory = new TestDirectory();
        var file = SettingsPath(directory.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string original = "{\"modelProvider\":\"other\",\"unrelated\":{\"marker\":\"synthetic preserved field\"}}";
        await File.WriteAllTextAsync(file, original);
        using var snapshot = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(snapshot);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await release.Task;
            return Record.Exception(() => AntigravityAdapter.EnsureApiKeySettings(directory.Root));
        })).ToArray();

        release.TrySetResult();
        var failures = await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(original, await reader.ReadToEndAsync());
        var current = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal("gemini", current["modelProvider"]!.GetValue<string>());
        Assert.Equal("synthetic preserved field", current["unrelated"]!["marker"]!.GetValue<string>());
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public async Task ValidSettingsPreserveUnrelatedNestedFields()
    {
        using var directory = new TestDirectory();
        var file = SettingsPath(directory.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{\"modelProvider\":\"other\",\"unrelated\":{\"values\":[1,2],\"flag\":true}}");

        AntigravityAdapter.EnsureApiKeySettings(directory.Root);

        var current = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal("gemini", current["modelProvider"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"values\":[1,2],\"flag\":true}"), current["unrelated"]));
    }

    private static string SettingsPath(string root) => Path.Combine(root, ".gemini", "antigravity-cli", "settings.json");
}
