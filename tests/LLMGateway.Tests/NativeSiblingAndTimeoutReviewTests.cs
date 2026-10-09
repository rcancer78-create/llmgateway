using System.Diagnostics;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class NativeSiblingAndTimeoutReviewTests
{
    [Fact]
    public async Task CursorStatusFailureDrainsBothActualNativeProcessesBeforeReturning()
    {
        var directory = new TestDirectory();
        var account = await CursorAccountAsync(directory, failStatus: true);
        var adapter = new CursorAdapter(new ExecutableResolver(), new GatewayOptions { WorkspaceDirectory = directory.Root }, NullLogger<CursorAdapter>.Instance);
        using var cancellation = new CancellationTokenSource();
        var operation = adapter.GetStatusAsync(account, cancellation.Token);
        Process? status = null;
        Process? about = null;
        Exception? primaryFailure = null;
        try
        {
            status = await OpenMarkedProcessAsync(directory.GetPath("status.pid"));
            about = await OpenMarkedProcessAsync(directory.GetPath("about.pid"));
            await File.WriteAllTextAsync(directory.GetPath("release-status"), "owned handles acquired");
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(GatewayErrorKind.Upstream, Assert.IsType<GatewayException>(failure).Kind);
            Assert.True(status.HasExited, "The failing command must finish its own cleanup.");
            Assert.True(about.HasExited, "Status must not return while its already-started sibling remains alive.");
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
                await ReapOwnedAsync(status);
                await ReapOwnedAsync(about);
                // Root exit can precede retirement of the sibling's pipe/job handles on the baseline.
                // Retry only this fixture's owned directory; never hide a primary assertion.
                await DisposeOwnedDirectoryAsync(directory);
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                throw new AggregateException("Native fixture cleanup failed after its primary assertion.", primaryFailure, cleanupFailure);
            }
        }
    }

    [Fact]
    public async Task CursorSuccessfulStatusStillCombinesBothNativeResponses()
    {
        using var directory = new TestDirectory();
        var account = await CursorAccountAsync(directory, failStatus: false);
        var adapter = new CursorAdapter(new ExecutableResolver(), new GatewayOptions { WorkspaceDirectory = directory.Root }, NullLogger<CursorAdapter>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await adapter.GetStatusAsync(account, deadline.Token);

        Assert.Equal(AccountAvailability.Ready, result.Availability);
        Assert.Equal("review@example.invalid", result.Identity?.Email);
        Assert.Equal("synthetic-version", result.ClientVersion);
    }

    [Fact]
    public async Task TimedOutNativeCommandPreservesCollectedStdoutAndConfirmsRootCleanup()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("partial.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$marker)
            [Console]::Out.Write('diagnostic:' + ('x' * (16384 - 11)))
            [Console]::Out.Flush()
            [IO.File]::WriteAllText($marker, [string]$PID)
            Start-Sleep -Seconds 60
            """);
        using var cleanup = new CancellationTokenSource();
        var operation = NativeProcess.RunAsync(Launch(directory, script, directory.GetPath("command.pid")), TimeSpan.FromSeconds(5), cleanup.Token);
        Process? owned = null;
        try
        {
            owned = await OpenMarkedProcessAsync(directory.GetPath("command.pid"));
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(9));

            Assert.True(result.TimedOut);
            Assert.False(result.Success);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal("diagnostic:" + new string('x', 16384 - 11), result.StandardOutput);
            Assert.True(owned.HasExited);
        }
        finally
        {
            cleanup.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            await ReapOwnedAsync(owned);
        }
    }

    [Fact]
    public async Task SuccessfulNativeCommandRetainsOutputAndSuccessClassification()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("successful.ps1");
        await File.WriteAllTextAsync(script, "[Console]::Out.Write('synthetic diagnostic')");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await NativeProcess.RunAsync(Launch(directory, script), TimeSpan.FromSeconds(5), deadline.Token);

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal("synthetic diagnostic", result.StandardOutput);
    }

    private static async Task<AccountProfile> CursorAccountAsync(TestDirectory directory, bool failStatus)
    {
        // Exercise the real npm-wrapper resolver and real Node processes; the shim is never executed by cmd.
        Assert.NotNull(new ExecutableResolver().Resolve("node"));
        var module = Path.Combine(directory.Root, "node_modules", "review");
        Directory.CreateDirectory(module);
        var script = Path.Combine(module, "cli.js");
        await File.WriteAllTextAsync(script, """
            const fs = require('fs');
            const path = require('path');
            const command = process.argv[2];
            const marker = path.join(process.cwd(), command + '.pid');
            fs.writeFileSync(marker, String(process.pid));
            if (process.env.REVIEW_FAIL_STATUS === '1') {
              if (command === 'about') {
                setInterval(() => {}, 1000);
              } else {
                const wait = setInterval(() => {
                  if (!fs.existsSync(path.join(process.cwd(), 'about.pid')) || !fs.existsSync(path.join(process.cwd(), 'release-status'))) return;
                  clearInterval(wait);
                  const chunk = Buffer.alloc(4096, 120);
                  for (let i = 0; i < 1025; i++) fs.writeSync(1, chunk);
                }, 10);
              }
            } else {
              const value = command === 'status'
                ? { isAuthenticated: true, userInfo: { email: 'review@example.invalid', userId: 123 } }
                : { cliVersion: 'synthetic-version', subscriptionTier: 'review' };
              fs.writeSync(1, JSON.stringify(value));
            }
            """);
        var shim = directory.GetPath("cursor-review.cmd");
        await File.WriteAllTextAsync(shim, "@node \"%~dp0\\node_modules\\review\\cli.js\" %*");
        var target = Assert.IsType<LaunchTarget>(new ExecutableResolver().Resolve(shim));
        Assert.Equal(LaunchKind.Direct, target.Kind);
        Assert.Equal(script, Assert.Single(target.PrefixArguments));
        return new AccountProfile
        {
            Id = "cursor-sibling-review", Provider = ProviderKind.Cursor, Executable = shim,
            WorkingDirectory = directory.Root,
            Environment = new Dictionary<string, string> { ["REVIEW_FAIL_STATUS"] = failStatus ? "1" : "0" }
        };
    }

    private static NativeLaunch Launch(TestDirectory directory, string script, params string[] arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(new(executable, [], LaunchKind.Direct, executable),
            new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments).ToArray(),
            new Dictionary<string, string?>(), directory.Root);
    }

    private static async Task<Process> OpenMarkedProcessAsync(string marker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                if (int.TryParse(await File.ReadAllTextAsync(marker, deadline.Token), out var pid) && pid > 0)
                {
                    var process = Process.GetProcessById(pid);
                    _ = process.SafeHandle;
                    return process;
                }
            }
            catch (IOException) { }
            await Task.Delay(10, deadline.Token);
        }
    }

    private static async Task ReapOwnedAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            // Join first: on the baseline the unobserved RunAsync sibling receives finally's cancellation.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.True(process.HasExited);
        }
        finally { process.Dispose(); }
    }

    private static async Task DisposeOwnedDirectoryAsync(TestDirectory directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                directory.Dispose();
                return;
            }
            catch (IOException) when (attempt < 40) { }
            catch (UnauthorizedAccessException) when (attempt < 40) { }
            await Task.Delay(50);
        }
    }
}
