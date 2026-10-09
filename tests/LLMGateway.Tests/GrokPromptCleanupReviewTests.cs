using System.Text;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class GrokPromptCleanupReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPromptWriteFailureRemovesOwnedFile(bool cancelled)
    {
        using var directory = new TestDirectory();
        var prompts = directory.GetPath("prompts");
        using var cancellation = new CancellationTokenSource();
        string? writtenPath = null;
        var adapter = Create(directory.Root, prompts, async (path, text, token) =>
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), token);
            Assert.Equal("synthetic partial prompt", await File.ReadAllTextAsync(path, token));
            writtenPath = path;
            if (cancelled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new IOException("synthetic failure after file creation");
        });

        var failure = await Record.ExceptionAsync(() => ConsumeAsync(adapter, directory, cancellation.Token));

        Assert.NotNull(writtenPath); // The writer really created a file; no unwritten-path mock.
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<IOException>(failure);
        Assert.False(File.Exists(writtenPath));
        Assert.Empty(Directory.EnumerateFiles(prompts));
    }

    [Fact]
    public async Task MissingOwnedExecutableStillCleansSuccessfullyWrittenPrompt()
    {
        using var directory = new TestDirectory();
        var prompts = directory.GetPath("prompts");
        var writes = 0;
        var adapter = Create(directory.Root, prompts, async (path, text, token) =>
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), token);
            writes++;
        });

        var failure = await Record.ExceptionAsync(() => ConsumeAsync(adapter, directory, CancellationToken.None));

        var unavailable = Assert.IsType<GatewayException>(failure);
        Assert.Equal(GatewayErrorKind.ProviderUnavailable, unavailable.Kind);
        Assert.Equal(1, writes);
        Assert.Empty(Directory.EnumerateFiles(prompts));
    }

    private static GrokAdapter Create(string root, string prompts,
        Func<string, string, CancellationToken, Task> writer) => new(new ExecutableResolver(),
        new GatewayOptions { WorkspaceDirectory = root }, NullLogger<GrokAdapter>.Instance, prompts, writer);

    private static async Task ConsumeAsync(GrokAdapter adapter, TestDirectory directory, CancellationToken token)
    {
        var account = new AccountProfile
        {
            Id = "grok-owned-cleanup-review", Provider = ProviderKind.Grok,
            Executable = directory.GetPath("definitely-not-installed-grok.exe"), WorkingDirectory = directory.Root
        };
        var request = new NativeChatRequest
        {
            Prompt = "synthetic partial prompt", WorkingDirectory = directory.Root, Timeout = TimeSpan.FromSeconds(5)
        };
        await foreach (var _ in adapter.RunChatAsync(account, request, token)) { }
    }
}
