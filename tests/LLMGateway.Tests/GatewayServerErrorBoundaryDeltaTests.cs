using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Server;
using Xunit;

namespace LLMGateway.Tests;

public sealed class GatewayServerErrorBoundaryDeltaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicTimeoutPreservesClassificationAndHidesActualOwnedNativeStderr(bool sanitize)
    {
        using var directory = new TestDirectory();
        var adapter = new NativeTimeoutAdapter(directory.Root);
        using var gateway = CreateGateway(directory.Root, adapter);
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new GatewayServerOptions { ApiKey = "synthetic-owned-key", SanitizeErrors = sanitize });
        using var http = CreateHttp(server.Url);
        try
        {
            using var response = await http.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = "codex/owned/gpt-5.5",
                messages = new[] { new { role = "user", content = "synthetic request" } }
            });
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.NotNull(adapter.NativeError);
            Assert.Equal(GatewayErrorKind.Timeout, adapter.NativeError.Kind);
            Assert.Contains(NativeTimeoutAdapter.PrivateMarker, adapter.NativeError.Message, StringComparison.Ordinal);
            Assert.Contains(directory.Root, adapter.NativeError.Message, StringComparison.Ordinal);
            Assert.NotNull(adapter.OwnedProcess);
            Assert.True(adapter.OwnedProcess.HasExited);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            Assert.Equal("api_error", error.GetProperty("type").GetString());
            Assert.Equal("timeout", error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            if (sanitize)
            {
                Assert.DoesNotContain(NativeTimeoutAdapter.PrivateMarker, message, StringComparison.Ordinal);
                Assert.DoesNotContain(directory.Root, message, StringComparison.Ordinal);
            }
            else Assert.Contains(NativeTimeoutAdapter.PrivateMarker, message, StringComparison.Ordinal);
        }
        finally { await adapter.ReleaseOwnedProcessAsync(); }
    }

    [Theory]
    [InlineData("outage", HttpStatusCode.ServiceUnavailable)]
    [InlineData("reject", HttpStatusCode.Unauthorized)]
    [InlineData("accept", HttpStatusCode.OK)]
    public async Task PublicValidatorDistinguishesInfrastructureFailureFromRejectedCredentials(
        string decision, HttpStatusCode expected)
    {
        using var directory = new TestDirectory();
        var adapter = new ProbeCountingAdapter();
        using var gateway = CreateGateway(directory.Root, adapter);
        var validations = 0;
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new GatewayServerOptions
            {
                RequireApiKey = true,
                ApiKeyValidator = (key, _) =>
                {
                    Assert.Equal("synthetic-owned-key", key);
                    Interlocked.Increment(ref validations);
                    return decision == "outage"
                        ? Task.FromException<bool>(new IOException("owned-validator-failure " + directory.Root))
                        : Task.FromResult(decision == "accept");
                }
            });
        using var http = CreateHttp(server.Url);
        using var response = await http.GetAsync("/v1/models");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, validations);
        Assert.Equal(decision == "accept", adapter.ProbeCalls > 0);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (decision == "accept") Assert.True(document.RootElement.TryGetProperty("data", out _));
        else
        {
            var error = document.RootElement.GetProperty("error");
            Assert.Equal(decision == "outage" ? "provider_unavailable" : "invalid_api_key",
                error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            Assert.DoesNotContain("owned-validator-failure", message, StringComparison.Ordinal);
            Assert.DoesNotContain(directory.Root, message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-owned-key", message, StringComparison.Ordinal);
        }
    }

    private static LlmGateway CreateGateway(string root, IProviderAdapter adapter) => new(
        JsonAccountStore.InMemory([new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]),
        [adapter], new GatewayOptions { WorkspaceDirectory = root });

    private static HttpClient CreateHttp(string url)
    {
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-owned-key");
        return http;
    }

    private class ProbeCountingAdapter : IProviderAdapter
    {
        private readonly FakeAdapter _metadata = new();
        private int _probes;
        public int ProbeCalls => Volatile.Read(ref _probes);
        public ProviderKind Provider => _metadata.Provider;
        public string DisplayName => _metadata.DisplayName;
        public string DefaultExecutable => _metadata.DefaultExecutable;
        public ProviderCapabilities Capabilities => _metadata.Capabilities;
        public string? ResolveExecutable(AccountProfile account) => _metadata.ResolveExecutable(account);
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token)
        { Interlocked.Increment(ref _probes); return _metadata.GetStatusAsync(account, token); }
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token)
        { Interlocked.Increment(ref _probes); return _metadata.ListModelsAsync(account, token); }
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => _metadata.GetQuotaAsync(account, token);
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => _metadata.StartInteractiveLoginAsync(account, token);
        public IEnumerable<AccountProfile> DiscoverProfiles() => _metadata.DiscoverProfiles();
        public virtual IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) =>
            _metadata.RunChatAsync(account, request, token);
    }

    private sealed class NativeTimeoutAdapter(string root) : ProbeCountingAdapter
    {
        public const string PrivateMarker = "owned-private-native-stderr";
        public GatewayException? NativeError { get; private set; }
        public Process? OwnedProcess { get; private set; }
        public override async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            var script = Path.Combine(root, "owned-timeout.ps1");
            var ready = Path.Combine(root, "owned-native.pid");
            var consumed = ready + ".consumed";
            static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
            await File.WriteAllTextAsync(script,
                "[Console]::Error.WriteLine('" + PrivateMarker + " " + Literal(root) + "'); " +
                "[Console]::Error.Write(('d' * 8192)); [Console]::Error.Flush(); " +
                "[IO.File]::WriteAllText('" + Literal(ready + ".tmp") + "', $PID.ToString()); " +
                "[IO.File]::Move('" + Literal(ready + ".tmp") + "', '" + Literal(ready) + "'); " +
                // Native named-pipe Flush may wait for consumption; hold the response, not stdin.
                "$_request = [Console]::In.ReadLine(); " +
                "[IO.File]::WriteAllText('" + Literal(consumed + ".tmp") + "', 'consumed'); " +
                "[IO.File]::Move('" + Literal(consumed + ".tmp") + "', '" + Literal(consumed) + "'); " +
                "while ($true) { [Threading.Thread]::Sleep(100) };", new UTF8Encoding(true), token);
            var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            await using var rpc = JsonRpcStdioClient.Start(new NativeLaunch(
                new LaunchTarget(shell, [], LaunchKind.Direct, shell),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
                new Dictionary<string, string?>(), root));
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Owned fixture did not publish readiness.");
                await Task.Delay(20, token);
            }
            OwnedProcess = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, token), System.Globalization.CultureInfo.InvariantCulture));
            _ = OwnedProcess.SafeHandle;
            JsonElement result;
            try { result = await rpc.RequestAsync("owned_timeout", null, TimeSpan.FromSeconds(1), token); }
            catch (GatewayException error)
            {
                Assert.True(File.Exists(consumed), "The owned native fixture must consume the actual RPC request before timing out.");
                NativeError = error;
                throw;
            }
            yield return NativeChatEvent.Delta(result.GetRawText());
        }

        public async Task ReleaseOwnedProcessAsync()
        {
            if (OwnedProcess is not { } process) return;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { process.Dispose(); }
        }
    }
}
