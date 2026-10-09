using LLMGateway.Core;
using LLMGateway.Native;

namespace LLMGateway.Tests;

public sealed class StandaloneRpcReviewRegressionTests
{
    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":42}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":")]
    [InlineData("{\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"error\":{\"code\":-32000,\"message\":\"owned\"}}")]
    public async Task InvalidRpcShapesPromptlyFailThePendingRequestAndPermitDisposal(string response)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-rpc-shape-");
        try
        {
            await using var fixture = await RpcFixture.CreateAsync(root.FullName, response);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var request = fixture.Client.RequestAsync("owned-fixture", null, TimeSpan.FromSeconds(8), stop.Token);
            Exception? requestFailure = null;
            Exception? disposalFailure = null;
            try
            {
                // Startup has completed before the request deadline begins. A malformed
                // response must still fail promptly, independently of a later fixture marker.
                requestFailure = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            finally
            {
                stop.Cancel();
                await Record.ExceptionAsync(() => request);
                disposalFailure = await Record.ExceptionAsync(() => fixture.Client.DisposeAsync().AsTask());
            }
            var classified = Assert.IsType<GatewayException>(requestFailure);
            Assert.Equal(GatewayErrorKind.Upstream, classified.Kind);
            Assert.Null(classified.JsonRpcErrorCode);
            Assert.Null(disposalFailure);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(-32000, "model not found", false)]
    [InlineData(-32000, "executable not found", false)]
    [InlineData(-32601, "opaque fixture error", true)]
    [InlineData(-32601, "method not found", true)]
    public async Task RpcCompatibilityFallbackUsesTheErrorCodeRatherThanUnrelatedNotFoundText(int code, string message, bool missingMethod)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-rpc-code-");
        try
        {
            var response = System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, error = new { code, message } });
            await using var fixture = await RpcFixture.CreateAsync(root.FullName, response);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var error = await Assert.ThrowsAsync<GatewayException>(() =>
                fixture.Client.RequestAsync("owned-fixture", null, TimeSpan.FromSeconds(8), stop.Token));
            Assert.Equal(missingMethod, RpcErrors.IsMissingMethod(error));
        }
        finally { root.Delete(true); }
    }

    private sealed class RpcFixture : IAsyncDisposable
    {
        private RpcFixture(JsonRpcStdioClient client) => Client = client;
        public JsonRpcStdioClient Client { get; }

        public static async Task<RpcFixture> CreateAsync(string root, string response)
        {
            var script = Path.Combine(root, "owned-rpc.ps1");
            var ready = Path.Combine(root, "ready");
            var quotedReady = ready.Replace("'", "''");
            await File.WriteAllTextAsync(script,
                "$ErrorActionPreference = 'Stop'\n"
                + "[IO.File]::WriteAllText('" + quotedReady + ".tmp', 'ready')\n"
                + "[IO.File]::Move('" + quotedReady + ".tmp', '" + quotedReady + "')\n"
                + "$null = [Console]::In.ReadLine()\n[Console]::Out.WriteLine('" + response.Replace("'", "''") + "')\n[Console]::Out.Flush()\nStart-Sleep -Seconds 60\n");
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var launch = new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), root);
            var fixture = new RpcFixture(JsonRpcStdioClient.Start(launch));
            try
            {
                // Parallel subprocess tests under coverage can delay a cold PowerShell
                // startup. Give startup its own budget without relaxing protocol deadlines.
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(ready)) await Task.Delay(20, startup.Token);
                return fixture;
            }
            catch (OperationCanceledException ex)
            {
                await fixture.DisposeAsync();
                throw new TimeoutException("The owned RPC fixture did not become ready within 30 seconds.", ex);
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The shape regression captures disposal's classified outcome separately; cleanup is idempotent.
            await Record.ExceptionAsync(() => Client.DisposeAsync().AsTask());
        }
    }
}
