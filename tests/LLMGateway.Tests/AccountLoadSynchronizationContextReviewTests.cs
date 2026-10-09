using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class AccountLoadSynchronizationContextReviewTests
{
    [Fact]
    public async Task SmallNewAccountFileLoadDoesNotRequirePumpingItsCallingSynchronizationContext()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-load-context-");
        var context = new HeldContext();
        var loaded = new TaskCompletionSource<JsonAccountStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = Path.Combine(root.FullName, "accounts.json");
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                loaded.TrySetResult(JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []));
            }
            catch (Exception ex) { loaded.TrySetException(ex); }
            finally { SynchronizationContext.SetSynchronizationContext(null); exited.TrySetResult(); }
        }) { IsBackground = true, Name = "Owned account Load calling context" };
        thread.Start();
        try
        {
            var transition = await Task.WhenAny(loaded.Task, context.FirstPost.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (transition == context.FirstPost.Task)
            {
                // Hold the actual captured continuation: a synchronous public Load cannot depend on
                // the caller's blocked context pump. This delay does not simulate or replace file IO.
                await Task.Delay(100);
                Assert.True(loaded.Task.IsCompleted,
                    $"Public Load has not returned and requires its blocked calling context; queued continuations: {context.Pending}.");
            }
            var store = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(store.GetAll());
            Assert.True(File.Exists(file));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            // Release queued real continuations only during teardown, so the actual writer/thread
            // settles after a meaningful RED without leaking a worker or touching another profile.
            var clock = Stopwatch.StartNew();
            while (!exited.Task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                context.ReleasePending();
                await Task.Delay(10);
            }
            context.ReleasePending();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Record.ExceptionAsync(() => loaded.Task);
            root.Delete(true);
        }
    }

    private sealed class HeldContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public TaskCompletionSource FirstPost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pending => _pending.Count;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            FirstPost.TrySetResult();
        }
        public void ReleasePending()
        {
            while (_pending.TryDequeue(out var continuation)) continuation.Callback(continuation.State);
        }
    }
}
