using System.Diagnostics;
using LLMGateway.Core;
using LLMGateway.Native;

namespace LLMGateway.Tests;

public sealed class NativeProcessLineBoundsReviewTests
{
    private const int MaximumLineChars = 4 * 1024 * 1024;

    [Fact]
    public async Task OversizedUnterminatedLineIsRefusedBeforeNativeExitAndPhysicalCleanupIsConfirmed()
    {
        using var directory = new TestDirectory();
        var launch = await LargeLineLaunchAsync(directory, rpc: false);
        await using var process = NativeProcess.Start(launch);
        using var cancellation = new CancellationTokenSource();
        await using var lines = process.ReadLinesAsync(cancellation.Token).GetAsyncEnumerator();
        var read = lines.MoveNextAsync().AsTask();
        try
        {
            await WaitForMarkerAsync(directory.GetPath("emitted.pid"));
            var failure = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
            var bound = Assert.IsType<GatewayException>(failure);
            Assert.Equal(GatewayErrorKind.Upstream, bound.Kind);
            Assert.False(process.HasExited, "A line limit must fail before waiting for newline or root exit.");
        }
        finally
        {
            cancellation.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            var stopped = await process.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.RootExitConfirmed);
            Assert.True(stopped.ContainmentEstablished);
            Assert.True(stopped.ContainedTreeExitConfirmed);
        }
    }

    [Fact]
    public async Task OversizedRpcLineFailsPendingRequestAsProtocolFailureBeforeItsTimeout()
    {
        using var directory = new TestDirectory();
        var launch = await LargeLineLaunchAsync(directory, rpc: true);
        await using var rpc = JsonRpcStdioClient.Start(launch);
        using var cancellation = new CancellationTokenSource();
        var request = rpc.RequestAsync("synthetic/review", null, TimeSpan.FromSeconds(15), cancellation.Token);
        Process? owned = null;
        try
        {
            var marker = directory.GetPath("emitted.pid");
            await WaitForMarkerAsync(marker);
            owned = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            _ = owned.SafeHandle;
            var failure = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
            var bound = Assert.IsType<GatewayException>(failure);
            Assert.Equal(GatewayErrorKind.Upstream, bound.Kind);
            Assert.False(owned.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await request.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            await rpc.DisposeAsync();
            if (owned is not null)
            {
                Assert.True(owned.HasExited);
                owned.Dispose();
            }
        }
    }

    [Fact]
    public async Task CompleteBufferedLineIsDeliveredWhileNativeTailIsStillHeld()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("incremental.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$marker)
            [Console]::Out.Write('first' + ('x' * 1018) + [char]10)
            [Console]::Out.Flush()
            [IO.File]::WriteAllText($marker, [string]$PID)
            Start-Sleep -Seconds 60
            """);
        await using var process = NativeProcess.Start(Launch(directory, script, directory.GetPath("emitted.pid")));
        using var cancellation = new CancellationTokenSource();
        await using var lines = process.ReadLinesAsync(cancellation.Token).GetAsyncEnumerator();
        var read = lines.MoveNextAsync().AsTask();
        try
        {
            await WaitForMarkerAsync(directory.GetPath("emitted.pid"));
            Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("first" + new string('x', 1018), lines.Current);
            Assert.False(process.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            var stopped = await process.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.RootExitConfirmed);
            Assert.True(stopped.ContainedTreeExitConfirmed);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(1310)]
    public async Task LineDelimitersAndTrailingPartialLineArePreserved(int separator)
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("delimiters.ps1");
        await File.WriteAllTextAsync(script, """
            param([int]$separator)
            $ending = if ($separator -eq 1310) { [string][char]13 + [char]10 } else { [string][char]$separator }
            [Console]::Out.Write('first' + $ending + $ending + 'second' + $ending + 'tail')
            """);
        await using var process = NativeProcess.Start(Launch(directory, script, separator.ToString()));
        var observed = new List<string>();
        await foreach (var line in process.ReadLinesAsync(CancellationToken.None)) observed.Add(line);

        Assert.Equal(new[] { "first", "second", "tail" }, observed);
        Assert.Equal(0, await process.WaitForExitAsync(CancellationToken.None));
    }

    private static async Task<NativeLaunch> LargeLineLaunchAsync(TestDirectory directory, bool rpc)
    {
        var script = directory.GetPath("oversized.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$marker, [int]$chunks, [int]$rpc)
            if ($rpc -eq 1) { $null = [Console]::In.ReadLine() }
            $chunk = 'x' * 4096
            for ($i = 0; $i -lt $chunks; $i++) { [Console]::Out.Write($chunk) }
            [Console]::Out.Write('x')
            [Console]::Out.Flush()
            [IO.File]::WriteAllText($marker, [string]$PID)
            Start-Sleep -Seconds 60
            """);
        return Launch(directory, script, directory.GetPath("emitted.pid"), (MaximumLineChars / 4096).ToString(), rpc ? "1" : "0");
    }

    private static NativeLaunch Launch(TestDirectory directory, string script, params string[] arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(new(executable, [], LaunchKind.Direct, executable),
            new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments).ToArray(),
            new Dictionary<string, string?>(), directory.Root);
    }

    private static async Task WaitForMarkerAsync(string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                if (int.TryParse(await File.ReadAllTextAsync(marker, timeout.Token), out var pid) && pid > 0) return;
            }
            catch (IOException) { }
            await Task.Delay(10, timeout.Token);
        }
    }
}
