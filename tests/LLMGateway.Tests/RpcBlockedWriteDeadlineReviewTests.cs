using System.Diagnostics;
using LLMGateway.Core;
using LLMGateway.Native;

namespace LLMGateway.Tests;

public sealed class RpcBlockedWriteDeadlineReviewTests
{
    [Fact]
    public async Task RequestDeadlineIncludesAnActualPipeWriteToANativeChildThatNeverReadsInput()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-rpc-blocked-write-");
        JsonRpcStdioClient? client = null;
        Process? child = null;
        Task? request = null;
        using var cleanup = new CancellationTokenSource();
        try
        {
            var marker = Path.Combine(root.FullName, "ready.pid");
            var script = Path.Combine(root.FullName, "owned-no-stdin.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$marker)
                [IO.File]::WriteAllText($marker + '.tmp', [string]$PID)
                [IO.File]::Move($marker + '.tmp', $marker)
                # Never read stdin. The real redirected OS pipe cannot hold the request below.
                Start-Sleep -Seconds 60
                """);
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            client = JsonRpcStdioClient.Start(new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["-NoProfile", "-NonInteractive", "-File", script, marker], new Dictionary<string, string?>(), root.FullName));
            using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                while (!File.Exists(marker)) await Task.Delay(20, startup.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            Assert.False(child.HasExited);
            request = client.RequestAsync("owned-blocked-write", new { padding = new string('x', 8 * 1024 * 1024) },
                TimeSpan.FromSeconds(1), cleanup.Token);
            var failure = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(4)));
            var classified = Assert.IsType<GatewayException>(failure);
            Assert.Equal(GatewayErrorKind.Timeout, classified.Kind);
            // Request timeout does not claim the remote/native operation has physically terminated.
            await client.DisposeAsync();
            Assert.True(child.HasExited);
        }
        finally
        {
            cleanup.Cancel();
            if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
            if (client is not null) await Record.ExceptionAsync(() => client.DisposeAsync().AsTask());
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            root.Delete(true);
        }
    }
}
