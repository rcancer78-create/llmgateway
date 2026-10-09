using System.Collections.Concurrent;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Tests;

public sealed class GrokPromptDeferredCleanupReviewTests
{
    [Fact]
    public async Task LockedPartialPromptRetainsCleanupOwnershipUntilTheOwnedFileIsReleased()
    {
        const string prompt = "synthetic held prompt";
        using var directory = new TestDirectory();
        var prompts = directory.GetPath("prompts");
        var logger = new CapturingLogger();
        FileStream? held = null;
        string? written = null;
        var adapter = new GrokAdapter(new ExecutableResolver(), new GatewayOptions { WorkspaceDirectory = directory.Root }, logger,
            prompts, async (path, text, token) =>
            {
                written = path;
                held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                await held.WriteAsync(Encoding.UTF8.GetBytes(text), token);
                held.Flush(flushToDisk: true);
                throw new IOException("synthetic partial write failure while file is held");
            });
        try
        {
            var original = await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(adapter, directory, prompt));
            Assert.Contains("synthetic partial write failure", original.Message);
            var locked = Assert.IsType<FileStream>(held);
            var ownedPath = Assert.IsType<string>(written);
            Assert.True(File.Exists(ownedPath));
            locked.Position = 0;
            var actual = new byte[Encoding.UTF8.GetByteCount(prompt)];
            await locked.ReadExactlyAsync(actual);
            Assert.Equal(prompt, Encoding.UTF8.GetString(actual)); // Failure really retained sensitive-shaped content.

            await locked.DisposeAsync();
            held = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (File.Exists(ownedPath) && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.False(File.Exists(ownedPath), "A failed immediate delete/wipe must retain an owned cleanup operation after return.");
            var warnings = logger.Messages.Where(item => item.Level >= LogLevel.Warning).ToArray();
            Assert.NotEmpty(warnings);
            Assert.All(warnings, item =>
            {
                Assert.DoesNotContain(prompt, item.Message);
                Assert.DoesNotContain(ownedPath, item.Message);
            });
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            if (written is not null) File.Delete(written); // Only this fixture's owned path, never a user prompt.
        }
    }

    private static async Task ConsumeAsync(GrokAdapter adapter, TestDirectory directory, string prompt)
    {
        var account = new AccountProfile
        {
            Id = "grok-held-cleanup-review", Provider = ProviderKind.Grok,
            Executable = directory.GetPath("not-installed-grok.exe"), WorkingDirectory = directory.Root
        };
        var request = new NativeChatRequest { Prompt = prompt, WorkingDirectory = directory.Root, Timeout = TimeSpan.FromSeconds(5) };
        await foreach (var _ in adapter.RunChatAsync(account, request, CancellationToken.None)) { }
    }

    private sealed class CapturingLogger : ILogger<GrokAdapter>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue((logLevel, formatter(state, exception)));
    }
}
