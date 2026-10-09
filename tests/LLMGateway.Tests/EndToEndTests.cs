using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.Client;
using LLMGateway.Server;

namespace LLMGateway.Tests;

internal sealed class FakeAdapter : IProviderAdapter
{
    public ProviderKind Provider => ProviderKind.Codex;
    public string DisplayName => "Fake Codex";
    public string DefaultExecutable => "fake";
    public ProviderCapabilities Capabilities { get; } = new(true, "fake", MultiAccountSupport.Isolated, "test", "CODEX_HOME", null, true, 100_000);
    public List<(string AccountId, NativeChatRequest Request)> Calls { get; } = [];

    public string? ResolveExecutable(AccountProfile account) => "fake.exe";

    public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken) =>
        Task.FromResult(new AccountStatus(AccountAvailability.Ready, new AccountIdentity($"{account.Id}@example.com", "plus", null), "1.0", null, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NativeModel>>([new NativeModel("gpt-5.5", "GPT-5.5", true), new NativeModel("gpt-5.4-mini", "GPT-5.4 mini")]);

    public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken) =>
        Task.FromResult(new QuotaSnapshot(account.Id, Provider, DateTimeOffset.UtcNow, AccountAvailability.Ready, true, "plus",
            [new QuotaBucket("codex.primary", "5 часов", 25, WindowMinutes: 300, ResetsAt: DateTimeOffset.UtcNow.AddHours(2), Unit: "%")], "fake", null));

    public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add((account.Id, request));
        if (request.Prompt.Contains("LIMIT", StringComparison.Ordinal))
        {
            yield return NativeChatEvent.Fail(GatewayErrorKind.RateLimited, "usage limit reached", DateTimeOffset.UtcNow.AddMinutes(30));
            yield break;
        }
        if (request.Prompt.Contains("get_time", StringComparison.Ordinal) && !request.Prompt.Contains("## Tool result", StringComparison.Ordinal))
        {
            yield return NativeChatEvent.Delta("<tool_calls>[{\"name\":\"get_time\",\"arguments\":{\"utc\":true}}]</tool_calls>");
            yield break;
        }
        if (request.Prompt.Contains("JSON object", StringComparison.Ordinal))
        {
            yield return NativeChatEvent.Delta("```json\n{\"ok\":true}\n```");
            yield break;
        }
        foreach (var part in new[] { "Hello", " from ", account.Id, " STOP ignored" })
        {
            await Task.Delay(5, cancellationToken);
            yield return NativeChatEvent.Delta(part);
        }
        yield return NativeChatEvent.Reported(new TokenUsage(11, 7, 18, 3));
    }

    public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;

    public IEnumerable<AccountProfile> DiscoverProfiles() => [];
}

public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly FakeAdapter _adapter = new();
    private LlmGateway _gateway = null!;
    private EmbeddedGatewayServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var store = JsonAccountStore.InMemory(
        [
            new AccountProfile { Id = "codex-default", DisplayName = "Default", Provider = ProviderKind.Codex, IsActive = true },
            new AccountProfile { Id = "codex-work", DisplayName = "Work", Provider = ProviderKind.Codex, ConfigDirectory = Path.Combine(Path.GetTempPath(), "llmgw-test-codex-work") }
        ]);
        var options = new GatewayOptions { WorkspaceDirectory = Path.Combine(Path.GetTempPath(), "llmgw-test-workspace") };
        _gateway = new LlmGateway(store, [_adapter], options);
        _server = await EmbeddedGatewayServer.StartAsync(_gateway, "http://127.0.0.1:0", new GatewayServerOptions { ApiKey = "secret" });
        _http = new HttpClient { BaseAddress = new Uri(_server.Url + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _gateway.Dispose();
    }

    private Task<HttpResponseMessage> Post(string path, string json) =>
        _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Requests_without_key_are_rejected()
    {
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };
        using var response = await anonymous.GetAsync("v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication_error", (await Json(response)).GetProperty("error").GetProperty("type").GetString());
        using var health = await anonymous.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Models_list_has_provider_account_model_ids()
    {
        using var response = await _http.GetAsync("v1/models");
        var json = await Json(response);
        var ids = json.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();

        Assert.Equal("list", json.GetProperty("object").GetString());
        Assert.Contains("codex/codex-default/gpt-5.5", ids);
        Assert.Contains("codex/codex-work/gpt-5.4-mini", ids);
        var model = json.GetProperty("data").EnumerateArray().First(m => m.GetProperty("id").GetString() == "codex/codex-work/gpt-5.5");
        Assert.Equal("codex-work", model.GetProperty("account_id").GetString());
        Assert.Equal("gpt-5.5", model.GetProperty("native_model").GetString());

        using var single = await _http.GetAsync("v1/models/codex/codex-work/gpt-5.5");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using var missing = await _http.GetAsync("v1/models/codex/codex-work/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Chat_completion_accepts_content_arrays_and_account_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent("""
                {"model":"codex/gpt-5.5","stop":[" STOP"],
                 "messages":[{"role":"system","content":"Be brief"},{"role":"user","content":[{"type":"text","text":"Hi"}]}]}
                """, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-LLM-Account", "codex-work");
        using var response = await _http.SendAsync(request);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chat.completion", json.GetProperty("object").GetString());
        var choice = json.GetProperty("choices")[0];
        Assert.Equal("Hello from codex-work", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("codex/codex-work/gpt-5.5", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("usage").GetProperty("prompt_tokens").GetInt32() > 0);
        Assert.Equal("codex-work", json.GetProperty("x_gateway").GetProperty("account_id").GetString());

        var call = Assert.Single(_adapter.Calls);
        Assert.Equal("codex-work", call.AccountId);
        Assert.Equal("gpt-5.5", call.Request.Model);
        Assert.Contains("Be brief", call.Request.Prompt);
    }

    [Fact]
    public async Task Streaming_sends_real_deltas_usage_and_done()
    {
        using var response = await Post("v1/chat/completions", """
            {"model":"codex/codex-default/gpt-5.5","stream":true,"stream_options":{"include_usage":true},
             "messages":[{"role":"user","content":"Hi"}]}
            """);
        var body = await response.Content.ReadAsStringAsync();
        var chunks = body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l[6..]).ToList();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("[DONE]", chunks[^1]);
        var parsed = chunks[..^1].Select(c => JsonDocument.Parse(c).RootElement).ToList();
        Assert.Equal("assistant", parsed[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        var text = string.Concat(parsed.SelectMany(p => p.GetProperty("choices").EnumerateArray())
            .Select(c => c.GetProperty("delta").TryGetProperty("content", out var content) ? content.GetString() : null));
        Assert.Equal("Hello from codex-default STOP ignored", text);
        Assert.True(parsed.Count(p => p.GetProperty("choices").GetArrayLength() > 0 && p.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out _)) >= 3);
        Assert.Contains(parsed, p => p.GetProperty("choices").GetArrayLength() > 0 && p.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == "stop");
        Assert.Equal(18, parsed[^1].GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Max_tokens_truncates_the_completion()
    {
        using var response = await Post("v1/chat/completions", """
            {"model":"codex","max_completion_tokens":1,"messages":[{"role":"user","content":"Hi"}]}
            """);
        var json = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var choice = json.GetProperty("choices")[0];
        Assert.Equal("Hell", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("length", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(1, json.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
    }

    [Fact]
    public async Task Embeddings_are_rejected_in_the_openai_error_shape()
    {
        using var response = await Post("v1/embeddings", """{"model":"codex","input":"hi"}""");
        var json = await Json(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sampling_parameters_are_ignored_by_default()
    {
        using var response = await Post("v1/chat/completions", """
            {"model":"codex","temperature":0.2,"top_p":1,"messages":[{"role":"user","content":"Hi"}]}
            """);
        var json = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from codex-default STOP ignored", json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Routing_errors_keep_http_status_even_when_streaming()
    {
        using var response = await Post("v1/chat/completions", """{"model":"codex/missing-account/x","stream":true,"messages":[{"role":"user","content":"Hi"}],"account_id":"missing-account"}""");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var limited = await Post("v1/chat/completions", """{"model":"codex","messages":[{"role":"user","content":"LIMIT"}]}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("rate_limit_exceeded", (await Json(limited)).GetProperty("error").GetProperty("code").GetString());
        var accounts = await _gateway.GetAccountsAsync();
        Assert.Equal(AccountAvailability.RateLimited, accounts.Single(a => a.Id == "codex-default").Availability);
        var quota = (await _gateway.GetQuotasAsync(false, "codex-default")).Single();
        Assert.Contains(quota.Buckets, b => b.ResetsAt is not null);
    }

    [Fact]
    public async Task Tool_calls_and_json_mode_round_trip()
    {
        using var response = await Post("v1/chat/completions", """
            {"model":"codex","messages":[{"role":"user","content":"What time is it?"}],
             "tools":[{"type":"function","function":{"name":"get_time","parameters":{"type":"object"}}}]}
            """);
        var choice = (await Json(response)).GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_time", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"utc\":true}", call.GetProperty("function").GetProperty("arguments").GetString());

        using var json = await Post("v1/chat/completions", """
            {"model":"codex","stream":true,"response_format":{"type":"json_object"},"messages":[{"role":"user","content":"status"}]}
            """);
        var content = string.Concat((await json.Content.ReadAsStringAsync()).Split('\n')
            .Where(l => l.StartsWith("data: {", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement.GetProperty("choices"))
            .Where(c => c.GetArrayLength() > 0 && c[0].GetProperty("delta").TryGetProperty("content", out _))
            .Select(c => c[0].GetProperty("delta").GetProperty("content").GetString()));
        Assert.Equal("{\"ok\":true}", content);
    }

    [Fact]
    public async Task Legacy_completions_endpoint_works()
    {
        using var response = await Post("v1/completions", """{"model":"codex","prompt":"Hi"}""");
        var json = await Json(response);
        Assert.Equal("text_completion", json.GetProperty("object").GetString());
        Assert.StartsWith("Hello from", json.GetProperty("choices")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Client_library_mode_over_http_covers_gateway_extensions()
    {
        using var client = new OpenAiGatewayClient(new Uri(_server.Url + "/"), "secret");

        var providers = await client.GetProvidersAsync();
        Assert.Equal("Fake Codex", Assert.Single(providers).DisplayName);

        var selected = await client.SelectAccountAsync("codex-work");
        Assert.True(selected.IsActive);
        var accounts = await client.GetAccountsAsync();
        Assert.Equal("codex-work", Assert.Single(accounts, a => a.IsActive).Id);

        var quotas = await client.GetQuotasAsync(true);
        var bucket = Assert.Single(quotas.First(q => q.AccountId == "codex-work").Buckets);
        Assert.Equal(25, bucket.UsedPercent);
        Assert.InRange(bucket.ResetIn!.Value.TotalMinutes, 100, 121);

        var checkedAccount = await client.CheckAccountAsync("codex-work");
        Assert.Equal("codex-work@example.com", checkedAccount.Identity!.Email);

        var result = await client.CompleteAsync(new ChatRequest { Model = "codex", Messages = [ChatMessage.User("Hi")] });
        Assert.Equal("codex-work", result.AccountId);
        Assert.Equal("Hello from codex-work STOP ignored", result.Content);

        var deltas = new List<string>();
        ChatResult? completed = null;
        await foreach (var update in client.StreamAsync(new ChatRequest { Model = "codex/codex-default/gpt-5.4-mini", Messages = [ChatMessage.User("Hi")] }))
        {
            if (update.Kind == ChatUpdateKind.TextDelta) deltas.Add(update.Text!);
            if (update.Kind == ChatUpdateKind.Completed) completed = update.Result;
        }
        Assert.True(deltas.Count >= 3);
        Assert.Equal("codex-default", completed!.AccountId);
        Assert.Equal("gpt-5.4-mini", completed.NativeModel);
        Assert.Equal("Hello from codex-default STOP ignored", completed.Content);
        Assert.Equal(18, completed.Usage.TotalTokens);

        var added = await client.AddAccountAsync(new AccountProfile { Id = "codex-new", DisplayName = "New", Provider = ProviderKind.Codex, ConfigDirectory = "C:\\codex-new" });
        Assert.Equal("C:\\codex-new", added.ConfigDirectory);
        await client.RemoveAccountAsync("codex-new");
        Assert.DoesNotContain(await client.GetAccountsAsync(), a => a.Id == "codex-new");

        var error = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(new ChatRequest { Model = "no-such-model", Messages = [ChatMessage.User("Hi")] }));
        Assert.Equal(GatewayErrorKind.ModelNotFound, error.Kind);
    }
}
