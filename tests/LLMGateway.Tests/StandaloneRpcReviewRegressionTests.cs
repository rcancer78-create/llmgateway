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
            var marker = Path.Combine(root.FullName, "response-emitted");
            await using var fixture = await RpcFixture.CreateAsync(root.FullName, response, marker);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var request = fixture.Client.RequestAsync("owned-fixture", null, TimeSpan.FromSeconds(8), stop.Token);
            Exception? requestFailure = null;
            Exception? disposalFailure = null;
            try
            {
                await WaitForOwnedMarkerAsync(marker, stop.Token);
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
            await using var fixture = await RpcFixture.CreateAsync(root.FullName, response, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var error = await Assert.ThrowsAsync<GatewayException>(() =>
                fixture.Client.RequestAsync("owned-fixture", null, TimeSpan.FromSeconds(8), stop.Token));
            Assert.Equal(missingMethod, RpcErrors.IsMissingMethod(error));
        }
        finally { root.Delete(true); }
    }

    private static async Task WaitForOwnedMarkerAsync(string marker, CancellationToken token)
    {
        while (!File.Exists(marker)) await Task.Delay(20, token);
    }

    private sealed class RpcFixture : IAsyncDisposable
    {
        private RpcFixture(JsonRpcStdioClient client) => Client = client;
        public JsonRpcStdioClient Client { get; }

        public static async Task<RpcFixture> CreateAsync(string root, string response, string? marker)
        {
            var script = Path.Combine(root, "owned-rpc.ps1");
            var mark = marker is null ? string.Empty : "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', 'emitted')";
            await File.WriteAllTextAsync(script,
                "$null = [Console]::In.ReadLine()\n[Console]::Out.WriteLine('" + response.Replace("'", "''") + "')\n[Console]::Out.Flush()\n" + mark + "\nStart-Sleep -Seconds 60\n");
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var launch = new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), root);
            return new RpcFixture(JsonRpcStdioClient.Start(launch));
        }

        public async ValueTask DisposeAsync()
        {
            // The shape regression captures disposal's classified outcome separately; cleanup is idempotent.
            await Record.ExceptionAsync(() => Client.DisposeAsync().AsTask());
        }
    }
}
