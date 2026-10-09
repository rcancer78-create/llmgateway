using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using LLMGateway.Core;
using LLMGateway.Core.Client;
using LLMGateway.Server;
using LLMGateway.Native;
using Xunit;

namespace LLMGateway.Tests;

public sealed class GatewayReviewRegressionTests
{
    [Fact]
    public async Task BareModelMissRefreshesAnOtherwiseLiveCatalog()
    {
        using var directory = new TestDirectory();
        var adapter = new Adapter();
        using var gateway = Create(directory, adapter);
        await gateway.GetModelsAsync();
        adapter.Models = [new("new-model", "New model")];
        var result = await gateway.CompleteAsync(new() { Model = "new-model", Messages = [ChatMessage.User("fixture")] });
        Assert.Equal("new-model", result.NativeModel);
        Assert.Equal(2, adapter.CatalogCalls);
    }

    [Fact]
    public async Task DisposeDoesNotInvalidateAnAdmittedRequestAndRejectsNewRequests()
    {
        using var directory = new TestDirectory();
        var adapter = new Adapter { Wait = true };
        using var gateway = Create(directory, adapter);
        var running = gateway.CompleteAsync(Request());
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.Dispose(); gateway.Dispose();
        adapter.Release.TrySetResult();
        Assert.Equal("partial", (await running.WaitAsync(TimeSpan.FromSeconds(5))).Content);
        await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(Request()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidstreamFailureIsExplicitSanitizedAndHasNoSuccessTerminator(bool typed)
    {
        using var directory = new TestDirectory();
        var adapter = new Adapter { Error = typed ? new GatewayException(GatewayErrorKind.Upstream, "private-fixture") : new InvalidOperationException("private-fixture") };
        using var gateway = Create(directory, adapter);
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture" });
        using var client = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        { model = "codex/work/model", stream = true, messages = new[] { new { role = "user", content = "fixture" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("partial", content);
        Assert.Contains("\"error\"", content);
        Assert.DoesNotContain("private-fixture", content);
        Assert.DoesNotContain("[DONE]", content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientDoesNotConvertATruncatedStreamIntoSuccess(bool doneWithoutFinish)
    {
        var text = "data: {\"id\":\"chatcmpl-fixture\",\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n";
        if (doneWithoutFinish) text += "data: [DONE]\n\n";
        using var http = new HttpClient(new ResponseHandler(text)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiGatewayClient(http);
        await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            await foreach (var update in client.StreamAsync(Request())) Assert.NotEqual(ChatUpdateKind.Completed, update.Kind);
        });
    }

    [Fact]
    public void JsonFenceRemovalDoesNotTreatAnInternalBacktickAsTheClosingFence()
    {
        const string incompleteFence = "```json\n{\"text\":\"a ``` sequence\"}";
        Assert.Equal(incompleteFence, PromptBuilder.StripJsonFences(incompleteFence));
        Assert.Equal("{\"text\":\"a ``` sequence\"}", PromptBuilder.StripJsonFences(incompleteFence + "\n```"));
    }

    [Fact]
    public async Task RpcDisposalDrainsConcurrentWritersAndRejectsLaterCalls()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("blocked-rpc.ps1");
        await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 60");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        await using var client = JsonRpcStdioClient.Start(new(new(shell, [], LaunchKind.Direct, shell),
            ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), directory.Root));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requests = Enumerable.Range(0, 12).Select(_ => client.RequestAsync("fixture", new { text = new string('x', 256 * 1024) },
            TimeSpan.FromSeconds(10), deadline.Token)).ToArray();
        var disposal = client.DisposeAsync().AsTask();
        Assert.Same(disposal, client.DisposeAsync().AsTask());
        foreach (var request in requests) await Assert.ThrowsAsync<GatewayException>(() => request.WaitAsync(deadline.Token));
        await disposal.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<GatewayException>(() => client.RequestAsync("closed", null, TimeSpan.FromSeconds(1), deadline.Token));
        await Assert.ThrowsAsync<GatewayException>(() => client.NotifyAsync("closed", null, deadline.Token));
    }

    [Fact]
    public async Task QueuedAndCancelledRequestsDoNotRaceGatewayDisposal()
    {
        using var directory = new TestDirectory();
        var adapter = new Adapter { Wait = true };
        using var gateway = Create(directory, adapter);
        var first = gateway.CompleteAsync(Request());
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gateway.CompleteAsync(Request());
        using var cancelled = new CancellationTokenSource();
        var third = gateway.CompleteAsync(Request(), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        gateway.Dispose();
        adapter.Release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.Equal("partial", result.Content));
    }

    [Fact]
    public void ToolCallsPreserveMultipleBlocksAndRejectUnknownCalls()
    {
        ToolDefinition[] tools = [new("known", null, "{}")];
        const string block = "<tool_calls>[{\"name\":\"known\",\"arguments\":{}}]</tool_calls>";
        var parsed = ToolCalling.Parse(block + "\n" + block, tools);
        Assert.Equal(2, parsed.Calls.Count);
        Assert.Equal(string.Empty, parsed.Text);
        Assert.Throws<GatewayException>(() => ToolCalling.Parse(
            "<tool_calls>[{\"name\":\"known\"},{\"name\":\"unknown\"}]</tool_calls>", tools));
    }

    [Fact]
    public void OutputBudgetHandlesLargeLimitsAndDoesNotSplitSurrogatePairs()
    {
        Assert.Equal("fixture", OutputBudget.Fit("fixture", 0, int.MaxValue, out var large));
        Assert.False(large);
        Assert.Equal("abc", OutputBudget.Fit("abc😀tail", 0, 1, out var unicode));
        Assert.True(unicode);
    }

    [Fact]
    public async Task StatusCommandRejectsUnboundedStandardOutput()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("large-output.ps1");
        await File.WriteAllTextAsync(script, "[Console]::Out.Write(('x' * (5 * 1024 * 1024)))");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var error = await Assert.ThrowsAsync<GatewayException>(() => NativeProcess.RunAsync(new(new(shell, [], LaunchKind.Direct, shell),
            ["-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string?>(), directory.Root),
            TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
    }

    [Fact]
    public async Task ModelAndQuotaProbesShareABoundedConcurrencyLimit()
    {
        using var directory = new TestDirectory();
        var adapter = new ProbeAdapter();
        using var gateway = new LlmGateway(new Store(12), [adapter], new GatewayOptions { WorkspaceDirectory = directory.Root });
        var models = gateway.GetModelsAsync(refresh: true);
        var quotas = gateway.GetQuotasAsync(refresh: true);
        try
        {
            await adapter.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(adapter.Peak, 1, 4);
        }
        finally { adapter.Release.TrySetResult(); }
        Assert.Equal(12, (await models.WaitAsync(TimeSpan.FromSeconds(5))).Count);
        Assert.Equal(12, (await quotas.WaitAsync(TimeSpan.FromSeconds(5))).Count);
        Assert.InRange(adapter.Peak, 1, 4);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("missing_function")]
    public void ToolChoiceWithoutToolsIsNotSilentlyDiscarded(string choice)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<LLMGateway.Core.OpenAi.OpenAiChatRequest>(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}],\"tool_choice\":\"" + choice + "\"}", GatewayJson.Options)!;
        var request = LLMGateway.Core.OpenAi.OpenAiMapper.ToChatRequest(dto);
        Assert.Throws<GatewayException>(() => PromptBuilder.Build(request, 10000));
    }

    [Theory]
    [InlineData("\"top_p\":0.5")]
    [InlineData("\"presence_penalty\":1")]
    [InlineData("\"frequency_penalty\":1")]
    [InlineData("\"seed\":1")]
    public void LegacyCompletionsIgnoreSamplingParameters(string option)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<LLMGateway.Core.OpenAi.OpenAiCompletionRequest>(
            "{\"model\":\"auto\",\"prompt\":\"fixture\"," + option + "}", GatewayJson.Options)!;
        var request = LLMGateway.Core.OpenAi.OpenAiMapper.ToChatRequest(dto);
        Assert.Equal("fixture", request.Messages[0].Content);
    }

    [Fact]
    public void LegacyCompletionsRejectSuffix()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<LLMGateway.Core.OpenAi.OpenAiCompletionRequest>(
            "{\"model\":\"auto\",\"prompt\":\"fixture\",\"suffix\":\"end\"}", GatewayJson.Options)!;
        var error = Assert.Throws<GatewayException>(() => LLMGateway.Core.OpenAi.OpenAiMapper.ToChatRequest(dto));
        Assert.Equal(GatewayErrorKind.Unsupported, error.Kind);
    }

    [Theory]
    [InlineData("required", "plain reply")]
    [InlineData("known", "plain reply")]
    [InlineData("known", "<tool_calls>[{\"name\":\"other\",\"arguments\":{}}]</tool_calls>")]
    public async Task ExplicitToolChoiceCannotSilentlyDegrade(string choice, string reply)
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter { Reply = reply });
        var request = Request();
        request.ToolChoice = choice;
        request.Tools = [new("known", null, "{}"), new("other", null, "{}")];
        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(request));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
    }

    [Fact]
    public async Task ModelLookupPreservesModelIdCase()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture" });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        using var exact = await http.GetAsync("/v1/models/codex/work/model");
        using var wrongCase = await http.GetAsync("/v1/models/codex/work/MODEL");
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongCase.StatusCode);
    }

    [Fact]
    public async Task UnexpectedHttpFailureDoesNotExposeExceptionDetails()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter { Error = new InvalidOperationException("private-fixture-path") });
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture" });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        using var response = await http.PostAsJsonAsync("/v1/chat/completions", new
        { model = "codex/work/model", messages = new[] { new { role = "user", content = "fixture" } } });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private-fixture-path", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FailedCatalogDiscoveryDoesNotMarkFallbackAccountReady()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter { CatalogError = true });
        Assert.Equal("auto", Assert.Single(await gateway.GetModelsAsync()).NativeModel);
        Assert.Equal(AccountAvailability.Unknown, Assert.Single(await gateway.GetAccountsAsync()).Availability);
    }

    [Fact]
    public async Task StreamDoesNotStartUntilTheAccountSlotIsFree()
    {
        using var directory = new TestDirectory();
        var adapter = new Adapter { Wait = true };
        using var gateway = Create(directory, adapter);
        await using var first = gateway.StreamAsync(Request()).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ChatUpdateKind.Started, first.Current.Kind);
        await using var second = gateway.StreamAsync(Request()).GetAsyncEnumerator();
        var pending = second.MoveNextAsync().AsTask();
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        adapter.Release.TrySetResult();
        while (await first.MoveNextAsync()) { }
        await first.DisposeAsync();
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ChatUpdateKind.Started, second.Current.Kind);
    }

    [Fact]
    public async Task ValidationMessageSurvivesSanitizationAndHealthRequiresTheApiKey()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture", MaxRequestBodyBytes = 64 });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var health = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var anonymous = await http.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        using var invalid = await http.PostAsJsonAsync("/v1/chat/completions", new { model = "codex/work/model" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("messages", await invalid.Content.ReadAsStringAsync());
        using var oversized = await http.PostAsync("/v1/chat/completions", new StringContent(new string('x', 128)));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("размер", await oversized.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HttpClientAcceptsACompleteStreamWithoutPrivateRoutingMetadata()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ExposeRoutingMetadata = false, ApiKey = "fixture" });
        using var client = new OpenAiGatewayClient(new Uri(server.Url), "fixture");
        var updates = new List<ChatUpdate>();
        await foreach (var update in client.StreamAsync(Request())) updates.Add(update);
        Assert.Equal(ChatUpdateKind.Started, updates[0].Kind);
        Assert.Null(updates[0].AccountId);
        Assert.Null(updates[0].Provider);
        Assert.Equal(ChatUpdateKind.Completed, updates[^1].Kind);
        Assert.Equal("partial", updates[^1].Result!.Content);
    }

    [Fact]
    public async Task ResponseCanSatisfyNamedToolChoice()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter { Reply = "<tool_calls>[{\"name\":\"named\",\"arguments\":{}}]</tool_calls>" });
        var request = Request();
        request.ToolChoice = "named";
        request.Tools = [new("named", null, "{}")];
        var result = await gateway.CompleteAsync(request);
        Assert.Equal("named", Assert.Single(result.ToolCalls).Name);
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Theory]
    [InlineData("--mode agent")]
    [InlineData("--mode=default")]
    [InlineData("--mode unknown")]
    [InlineData("--mode")]
    [InlineData("-s workspace-write")]
    [InlineData("-s=danger-full-access")]
    [InlineData("-sworkspace-write")]
    [InlineData("-c sandbox_mode=\"danger-full-access\"")]
    [InlineData("-csandbox_mode=\"danger-full-access\"")]
    [InlineData("--config sandbox_mode=workspace-write")]
    [InlineData("--config=approval_policy=never")]
    [InlineData("--permission-mode default")]
    [InlineData("--approval-mode full-auto")]
    [InlineData("--ask-for-approval never")]
    [InlineData("-anever")]
    [InlineData("--mode plan --mode agent")]
    [InlineData("--sandbox read-only --sandbox workspace-write")]
    public void PolicyOverridesAreRejectedBeforeLaunch(string arguments) =>
        Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(CliArguments.Parse(arguments)));

    [Theory]
    [InlineData("--mode ask --color never")]
    [InlineData("--mode plan --sandbox read-only")]
    [InlineData("-sread-only -aon-request")]
    [InlineData("--model model-id -m model-id --effort high")]
    public void SafeExtraArgumentsRemainUsable(string arguments) => CliArguments.RejectUnsafe(CliArguments.Parse(arguments));

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    public void UnresolvedScriptLaunchIsRefusedBeforeProcessCreation(string extension)
    {
        using var directory = new TestDirectory();
        var launch = new NativeLaunch(new("missing-executable", [], LaunchKind.Script, "wrapper" + extension),
            ["plain prompt"], new Dictionary<string, string?>(), directory.Root);
        var error = Assert.Throws<GatewayException>(() => NativeProcess.Start(launch));
        Assert.Equal(GatewayErrorKind.Unsupported, error.Kind);
    }

    [Fact]
    public async Task UnauthenticatedLoopbackCannotUseApiOrMutateAccounts()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0");
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var models = await client.GetAsync("/v1/models");
        using var mutation = await client.PostAsJsonAsync("/v1/gateway/accounts", new { id = "unauthorized", executable = "arbitrary.exe" });
        Assert.Equal(HttpStatusCode.Unauthorized, models.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
        Assert.DoesNotContain(await gateway.GetAccountsAsync(), account => account.Id == "unauthorized");
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Origin", "null")]
    [InlineData("Host", "evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Sec-Fetch-Mode", "navigate")]
    public async Task DefaultServerRejectsBrowserRequestsEvenWithKey(string header, string value)
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture" });
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        using var response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static LlmGateway Create(TestDirectory directory, Adapter adapter) =>
        new(new Store(), [adapter], new GatewayOptions { WorkspaceDirectory = directory.Root, MaxConcurrentRequestsPerAccount = 1 });
    private static ChatRequest Request() => new() { Model = "codex/work/model", Messages = [ChatMessage.User("fixture")] };
    private sealed class ResponseHandler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
    }
    private sealed class Store(int count = 1) : IAccountStore
    {
        public long RevisionId => 1;
        public bool IsReadOnly => false;
        public string? PersistenceWarning => null;
        private readonly AccountProfile _account = new() { Id = "work", Provider = ProviderKind.Codex, DisplayName = "Fixture", IsActive = true };
        public IReadOnlyList<AccountProfile> GetAll() => Enumerable.Range(0, count).Select(i =>
        { var profile = _account.Clone(); if (i > 0) profile.Id += i; return profile; }).ToArray();
        public AccountProfile? Find(string id) => id == _account.Id ? _account.Clone() : null;
        public Task AddAsync(AccountProfile profile, CancellationToken token = default) => throw new NotSupportedException();
        public Task UpdateAsync(AccountProfile profile, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public Task SelectAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
    }
    private class Adapter : IProviderAdapter
    {
        public IReadOnlyList<NativeModel> Models { get; set; } = [new("model", "Fixture")];
        public int CatalogCalls;
        public bool CatalogError;
        public bool Wait;
        public string Reply = "partial";
        public Exception? Error;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Fixture";
        public string DefaultExecutable => "fixture";
        public ProviderCapabilities Capabilities { get; } = new(false, "fixture", MultiAccountSupport.Isolated, "fixture", null, null, false, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "fixture";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token)
        { CatalogCalls++; if (CatalogError) throw new IOException("catalog unavailable"); return Task.FromResult(Models); }
        public virtual Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Started.TrySetResult();
            if (Wait) await Release.Task.WaitAsync(token);
            yield return NativeChatEvent.Delta(Reply);
            if (Error is not null) throw Error;
        }
    }

    private sealed class ProbeAdapter : Adapter
    {
        private int _active;
        public int Peak;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Probe(CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Peak; } while (observed < active && Interlocked.CompareExchange(ref Peak, active, observed) != observed);
            if (active >= 4) FourStarted.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public override async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token)
        { await Probe(token); return Models; }
        public override async Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token)
        { await Probe(token); return QuotaSnapshot.Unsupported(account, AccountAvailability.Ready, null, "fixture", "fixture"); }
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("llmgw-review-").FullName;
    public string GetPath(string file) => Path.Combine(Root, file);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
