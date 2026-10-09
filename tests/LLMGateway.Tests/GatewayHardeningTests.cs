using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using LLMGateway.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Tests;

public sealed class GatewayHardeningTests
{
    [Fact]
    public void SamplingParametersAreIgnoredUnlessThePolicyRejectsThem()
    {
        var json = """{"model":"m","temperature":0,"top_p":1,"messages":[{"role":"user","content":"hi"}]}""";
        var ignored = OpenAiMapper.ToChatRequest(JsonSerializer.Deserialize<OpenAiChatRequest>(json, GatewayJson.Options)!);
        Assert.Equal("hi", ignored.Messages[0].Content);

        var strict = new GatewayOptions { UnsupportedSamplingParameters = UnsupportedSamplingParameterPolicy.Reject };
        var error = Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(
            JsonSerializer.Deserialize<OpenAiChatRequest>(json, GatewayJson.Options)!, options: strict));
        Assert.Equal(GatewayErrorKind.Unsupported, error.Kind);
    }

    [Fact]
    public async Task RejectPolicyIsAppliedOnTheHttpSurface()
    {
        using var gateway = Gateway(new ScriptedAdapter(_ => Array.Empty<NativeChatEvent>()));
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new GatewayServerOptions { ApiKey = "fixture" },
            new GatewayOptions { UnsupportedSamplingParameters = UnsupportedSamplingParameterPolicy.Reject });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture");
        using var response = await http.PostAsync("/v1/chat/completions", new StringContent(
            """{"model":"codex/owned/model","temperature":0,"messages":[{"role":"user","content":"hi"}]}""",
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void IncidentalRateLimitWordsAreNotAnAccountLimit()
    {
        Assert.Equal(GatewayErrorKind.Upstream, NativeErrorClassifier.Classify("retrying after 429"));
        Assert.Equal(GatewayErrorKind.Upstream, NativeErrorClassifier.Classify("context exceeded your limit"));
        Assert.Equal(GatewayErrorKind.Upstream, NativeErrorClassifier.Classify("quota"));
        using var structured = JsonDocument.Parse("""{"code":"rate_limit_exceeded","message":"retrying after 429"}""");
        Assert.Equal(GatewayErrorKind.RateLimited, NativeErrorClassifier.Classify(structured.RootElement, "retrying after 429"));
        using var other = JsonDocument.Parse("""{"status":500,"message":"retrying after 429"}""");
        Assert.Equal(GatewayErrorKind.Upstream, NativeErrorClassifier.Classify(other.RootElement, "retrying after 429"));
    }

    [Fact]
    public void ProviderCredentialVariablesAreRemovedExceptTheExplicitTarget()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = null,
            ["PATH"] = "C:\\tools"
        };
        AccountEnvironment.RemoveForeignCredentials(environment, keepVariable: "GEMINI_API_KEY", ["TEAM_API_KEY", "GEMINI_API_KEY"]);
        Assert.Null(environment["ANTHROPIC_API_KEY"]);
        Assert.Null(environment["TEAM_API_KEY"]);
        Assert.False(environment.ContainsKey("GEMINI_API_KEY"));
        Assert.Equal("C:\\tools", environment["PATH"]);
    }

    [Fact]
    public void NativeLoginDropsHostProviderKeys()
    {
        var adapter = new EnvProbe();
        var profile = new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, AuthMode = AccountAuthMode.NativeLogin };
        var environment = adapter.EnvironmentFor(profile);
        Assert.Null(environment["ANTHROPIC_API_KEY"]);
        Assert.Null(environment["CODEX_API_KEY"]);
        Assert.Null(environment["OPENAI_API_KEY"]);
        Assert.Null(environment["GEMINI_API_KEY"]);
        Assert.Null(environment["CURSOR_API_KEY"]);
    }

    [Fact]
    public void GatewayLaunchesDoNotLoadUserMcpServers()
    {
        var account = new AccountProfile { Id = "owned", Provider = ProviderKind.Codex };
        var request = new NativeChatRequest { Prompt = "hi", WorkingDirectory = "C:\\work", Timeout = TimeSpan.FromSeconds(30) };
        var codex = CodexAdapter.BuildChatArguments(account, request);
        Assert.Contains(codex, argument => argument == "mcp_servers={}");
        var claude = ClaudeAdapter.BuildChatArguments(account, request);
        Assert.Contains("--strict-mcp-config", claude);
        Assert.Contains(claude, argument => argument == "");
        var cursor = CursorAdapter.BuildChatArguments(account, request);
        Assert.DoesNotContain("--trust", cursor);
    }

    [Fact]
    public void UnsafeMcpArgumentsAreRejected()
    {
        var error = Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(["--mcp-config", "servers.json"]));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public async Task LimitWithoutResetTimeExpiresAndALaterSuccessClearsIt()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-limit-default-");
        var adapter = new ScriptedAdapter(prompt => prompt.Contains("FAIL", StringComparison.Ordinal)
            ? [NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "usage limit reached")]
            : [NativeChatEvent.Delta("ok")]);
        try
        {
            using var gateway = Gateway(adapter, root.FullName);
            var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(Chat("FAIL")));
            Assert.Equal(GatewayErrorKind.RateLimited, failure.Kind);
            Assert.NotNull(failure.RetryAt);
            Assert.InRange((failure.RetryAt!.Value - DateTimeOffset.UtcNow).TotalMinutes, 14, 16);
            var limited = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));
            Assert.Equal(AccountAvailability.RateLimited, limited.Availability);
            Assert.NotNull(Assert.Single(limited.Buckets, bucket => bucket.Name == "observed").ResetsAt);

            var success = await gateway.CompleteAsync(Chat("ok"));
            Assert.Equal("ok", success.Content);
            var restored = Assert.Single(await gateway.GetAccountsAsync());
            Assert.Equal(AccountAvailability.Ready, restored.Availability);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SelectingAnotherAccountDoesNotDropAnInFlightLimit()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-observation-");
        var adapter = new HeldAdapter();
        try
        {
            var store = JsonAccountStore.InMemory(
            [
                new AccountProfile { Id = "codex-work", Provider = ProviderKind.Codex, IsActive = true },
                new AccountProfile { Id = "codex-other", Provider = ProviderKind.Codex }
            ]);
            using var gateway = new LlmGateway(store, [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            var pending = gateway.CompleteAsync(new ChatRequest
            {
                Model = "codex/codex-work/model",
                Messages = [ChatMessage.User("owned")]
            });
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gateway.SelectAccountAsync("codex-other");
            adapter.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<GatewayException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GatewayErrorKind.RateLimited, error.Kind);
            var work = (await gateway.GetAccountsAsync()).Single(account => account.Id == "codex-work");
            Assert.Equal(AccountAvailability.RateLimited, work.Availability);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnknownProviderEntryDoesNotReplaceTheAccountFile()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-unknown-provider-");
        try
        {
            var file = Path.Combine(root.FullName, "accounts.json");
            var original = """{"version":2,"accounts":[{"id":"owned","provider":"not-a-provider"}]}""";
            await File.WriteAllTextAsync(file, original);
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            Assert.True(store.IsReadOnly);
            Assert.Equal(original, await File.ReadAllTextAsync(file));
            using var gateway = new LlmGateway(store, [], new GatewayOptions());
            Assert.Contains("поврежд", gateway.AccountStoreWarning);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnknownModelRefreshesTheCatalogOncePerCooldown()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-catalog-cooldown-");
        var adapter = new ScriptedAdapter(_ => [NativeChatEvent.Delta("ok")]);
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]),
                [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName, CatalogRefreshCooldownSeconds = 60 });
            await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(new ChatRequest { Model = "missing-one", Messages = [ChatMessage.User("hi")] }));
            await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(new ChatRequest { Model = "missing-two", Messages = [ChatMessage.User("hi")] }));
            Assert.Equal(1, adapter.ModelCalls);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SlotWaitEndsWithRetryAfter()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-slot-wait-");
        var adapter = new HeldAdapter();
        try
        {
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]),
                [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName, MaxConcurrentRequestsPerAccount = 1, SlotWaitSeconds = 1 });
            var first = gateway.CompleteAsync(Chat("held"));
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(Chat("queued")));
            Assert.Equal(GatewayErrorKind.ProviderUnavailable, error.Kind);
            Assert.NotNull(error.RetryAt);
            adapter.Release.TrySetResult();
            await Assert.ThrowsAsync<GatewayException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task StopAtTheStartIsAnEmptyStopNotAnUpstreamError()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-stop-empty-");
        var adapter = new ScriptedAdapter(_ => [NativeChatEvent.Delta("END more")]);
        try
        {
            using var gateway = Gateway(adapter, root.FullName);
            var request = Chat("hi");
            request.Stop = ["END"];
            var result = await gateway.CompleteAsync(request);
            Assert.Null(result.Content);
            Assert.Equal("stop", result.FinishReason);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDoesNotCutToolCallJson(bool finalOnly)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-stop-tools-");
        const string answer = "<tool_calls>[{\"name\":\"known\",\"arguments\":{\"a\":1}}]</tool_calls>";
        var adapter = new ScriptedAdapter(_ => [finalOnly ? NativeChatEvent.Final(answer) : NativeChatEvent.Delta(answer)]);
        try
        {
            using var gateway = Gateway(adapter, root.FullName);
            var request = Chat("hi");
            request.Stop = ["}"];
            request.Tools = [new ToolDefinition("known", null, "{}")];
            var result = await gateway.CompleteAsync(request);
            Assert.Equal("known", Assert.Single(result.ToolCalls).Name);
            Assert.Equal("tool_calls", result.FinishReason);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task TruncatedJsonObjectReportsLength()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-json-length-");
        var adapter = new ScriptedAdapter(_ => [NativeChatEvent.Delta("{\"ok\":true,\"pad\":\"xxxxxxxx\"}")]);
        try
        {
            using var gateway = Gateway(adapter, root.FullName);
            var request = Chat("hi");
            request.MaxOutputTokens = 1;
            request.ResponseFormat = new ResponseFormat(ResponseFormatKind.JsonObject);
            var result = await gateway.CompleteAsync(request);
            Assert.Equal("length", result.FinishReason);
            Assert.Equal("{\"ok", result.Content);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void PromptTruncationDropsAnOrphanToolResultWithItsCall()
    {
        var request = new ChatRequest
        {
            Messages =
            [
                ChatMessage.User(new string('a', 40)),
                new ChatMessage(ChatRole.Assistant, "") { ToolCalls = [new ToolCall("call_1", "known", "{}")] },
                new ChatMessage(ChatRole.Tool, new string('b', 40)) { ToolCallId = "call_1" },
                ChatMessage.User("last")
            ]
        };
        var prompt = PromptBuilder.Build(request, 250);
        Assert.DoesNotContain("call_1", prompt);
        Assert.Contains("last", prompt);
    }

    [Fact]
    public async Task AllowedOriginCanCallTheApi()
    {
        using var gateway = Gateway(new ScriptedAdapter(_ => Array.Empty<NativeChatEvent>()));
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new GatewayServerOptions { ApiKey = "fixture", AllowedOrigins = ["https://app.example"] });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://app.example");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        using var allowed = await http.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://evil.example");
        using var rejected = await http.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task RemoteCompletionWithoutGatewayMetadataIsUnknown()
    {
        using var http = new HttpClient(new FixedHandler("""
            {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"remote","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}
            """))
        { BaseAddress = new Uri("http://remote.example/") };
        using var client = new LLMGateway.Core.Client.OpenAiGatewayClient(http);
        var result = await client.CompleteAsync(new ChatRequest { Messages = [ChatMessage.User("hi")] });
        Assert.Equal(ProviderKind.Unknown, result.Provider);
    }

    private static LlmGateway Gateway(IProviderAdapter adapter, string? workspace = null) =>
        new(JsonAccountStore.InMemory([new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]),
            [adapter], new GatewayOptions { WorkspaceDirectory = workspace ?? Path.GetTempPath() });

    private static ChatRequest Chat(string text) => new()
    {
        Model = "codex/owned/model",
        Messages = [ChatMessage.User(text)]
    };

    private sealed class EnvProbe : NativeAdapterBase
    {
        public EnvProbe() : base(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance) { }
        public IReadOnlyDictionary<string, string?> EnvironmentFor(AccountProfile account) => BuildEnvironment(account);
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Probe";
        public override string DefaultExecutable => "probe";
        public override ProviderCapabilities Capabilities { get; } = new(false, "probe", MultiAccountSupport.Isolated, "probe", null, "CODEX_API_KEY", false, 1000);
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
    }

    private sealed class ScriptedAdapter(Func<string, IReadOnlyList<NativeChatEvent>> script) : IProviderAdapter
    {
        public int ModelCalls;
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Scripted";
        public string DefaultExecutable => "scripted";
        public ProviderCapabilities Capabilities { get; } = new(true, "scripted", MultiAccountSupport.Isolated, "scripted", null, null, false, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "scripted";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, null, null, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ModelCalls);
            return Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Model", true)]);
        }
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken) =>
            Task.FromResult(new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, AccountAvailability.Ready, true, null, [], "scripted", null));
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var item in script(request.Prompt)) yield return item;
        }
    }

    private sealed class HeldAdapter : IProviderAdapter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Held";
        public string DefaultExecutable => "held";
        public ProviderCapabilities Capabilities { get; } = new(true, "held", MultiAccountSupport.Isolated, "held", null, null, false, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "held";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountStatus(AccountAvailability.Ready, null, null, null, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Model", true)]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken) =>
            Task.FromResult(new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, AccountAvailability.Ready, true, null, [], "held", null));
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "usage limit reached", DateTimeOffset.UtcNow.AddMinutes(30));
        }
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
