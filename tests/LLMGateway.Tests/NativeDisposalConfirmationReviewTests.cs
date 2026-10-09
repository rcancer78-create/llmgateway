using System.Reflection;
using LLMGateway.Core;
using LLMGateway.Native;

namespace LLMGateway.Tests;

// Exercise callers with a cached unconfirmed observation, after actually stopping the owned
// process. These tests do not claim to reproduce an OS kill failure or a surviving child.
public sealed class NativeDisposalConfirmationReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessDisposalPublishesUnconfirmedStopToCaller(bool rootConfirmed, bool treeConfirmed)
    {
        using var directory = new TestDirectory();
        var process = NativeProcess.Start(await LaunchAsync(directory));
        try
        {
            await StopAndSubstituteObservationAsync(process, rootConfirmed, treeConfirmed);
            var disposal = process.DisposeAsync().AsTask();
            Assert.Same(disposal, process.DisposeAsync().AsTask());
            await AssertDispositionAsync(disposal, rootConfirmed && treeConfirmed);
        }
        finally { await Record.ExceptionAsync(() => process.DisposeAsync().AsTask()); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RpcDisposalPropagatesStopFailureAndStillRetiresItsChannel(bool rootConfirmed, bool treeConfirmed)
    {
        using var directory = new TestDirectory();
        var rpc = JsonRpcStdioClient.Start(await LaunchAsync(directory));
        var process = Assert.IsType<NativeProcess>(Field(typeof(JsonRpcStdioClient), "_process").GetValue(rpc));
        try
        {
            await StopAndSubstituteObservationAsync(process, rootConfirmed, treeConfirmed);
            var disposal = rpc.DisposeAsync().AsTask();
            Assert.Same(disposal, rpc.DisposeAsync().AsTask());
            await AssertDispositionAsync(disposal, rootConfirmed && treeConfirmed);
            var later = await Assert.ThrowsAsync<GatewayException>(() => rpc.NotifyAsync("owned/after-close", null, CancellationToken.None));
            Assert.Equal(GatewayErrorKind.Upstream, later.Kind);
        }
        finally { await Record.ExceptionAsync(() => rpc.DisposeAsync().AsTask()); }
    }

    private static async Task StopAndSubstituteObservationAsync(NativeProcess process, bool rootConfirmed, bool treeConfirmed)
    {
        var physicallyStopped = await process.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(physicallyStopped.RootExitConfirmed);
        Assert.True(physicallyStopped.ContainmentEstablished);
        Assert.True(physicallyStopped.ContainedTreeExitConfirmed);
        Assert.True(process.HasExited);
        var observation = new NativeProcessTermination(process.Id, true, rootConfirmed, rootConfirmed ? 0 : null)
        { ContainmentEstablished = true, ContainedTreeExitConfirmed = treeConfirmed };
        Field(typeof(NativeProcess), "_termination").SetValue(process, Task.FromResult(observation));
    }

    private static async Task AssertDispositionAsync(Task disposal, bool confirmed)
    {
        var failure = await Record.ExceptionAsync(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        if (confirmed) Assert.Null(failure);
        else
        {
            var error = Assert.IsType<GatewayException>(failure);
            Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
            Assert.Contains("не подтверждено", error.Message);
        }
    }

    private static FieldInfo Field(Type owner, string name) =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Owned regression fixture field unavailable.");

    private static async Task<NativeLaunch> LaunchAsync(TestDirectory directory)
    {
        var script = directory.GetPath("owned-disposal.ps1");
        await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 60");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(new(shell, [], LaunchKind.Direct, shell),
            ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), directory.Root);
    }
}
