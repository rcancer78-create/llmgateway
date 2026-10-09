using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;

namespace LLMGateway.Tests;

/// <summary>Samples are trimmed real outputs of the installed clients (Codex 0.160, cursor-agent, agy 1.1.23, grok 1.0.46).</summary>
public class NativeParserTests
{
    private static List<NativeChatEvent> Run(IChatLineParser parser, string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line =>
            {
                using var document = JsonDocument.Parse(line);
                return parser.Parse(document.RootElement.Clone()).ToList();
            })
            .ToList();

    private static string Text(IEnumerable<NativeChatEvent> events) =>
        string.Concat(events.Where(e => e.Kind == NativeChatEventKind.Text).Select(e => e.Text));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly AccountProfile Account = new() { Id = "acc", Provider = ProviderKind.Codex };

    [Fact]
    public void Codex_exec_ignores_config_warnings_and_reads_usage()
    {
        const string sample = """
            {"type":"thread.started","thread_id":"01a0"}
            {"type":"item.completed","item":{"id":"item_0","type":"error","message":"Codex is ignoring 1 unrecognized configuration setting."}}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_1","type":"reasoning","text":"thinking about pong"}}
            {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"pong"}}
            {"type":"turn.completed","usage":{"input_tokens":20998,"cached_input_tokens":13440,"output_tokens":5,"reasoning_output_tokens":0}}
            """;
        var events = Run(new CodexAdapter.ExecParser(), sample);

        Assert.Equal("pong", Text(events));
        Assert.Contains(events, e => e.Kind == NativeChatEventKind.Reasoning && e.Text == "thinking about pong");
        var usage = Assert.Single(events, e => e.Kind == NativeChatEventKind.Usage).Usage!;
        Assert.Equal(20998, usage.PromptTokens);
        Assert.Equal(13440, usage.CachedPromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.Error);
    }

    [Fact]
    public void Codex_transient_reconnect_is_not_an_error_but_turn_failed_is()
    {
        var events = Run(new CodexAdapter.ExecParser(), """
            {"type":"error","message":"Reconnecting... 1/5 (stream disconnected before completion)"}
            {"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again in 2h 5m."}}
            """);

        var error = Assert.Single(events);
        Assert.Equal(NativeChatEventKind.Error, error.Kind);
        Assert.Equal(GatewayErrorKind.RateLimited, error.ErrorKind);
        Assert.NotNull(error.RetryAt);
    }

    [Fact]
    public void Codex_rate_limits_use_resets_at_not_window_length()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var weekly = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds();
        var result = Json($$$"""
            {"rateLimitsByLimitId":{"codex":{"limitId":"codex","limitName":null,
              "primary":{"usedPercent":37,"windowDurationMins":300,"resetsAt":{{{reset}}}},
              "secondary":{"usedPercent":12.5,"windowDurationMins":10080,"resetsAt":{{{weekly}}}},
              "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"plus"} } }
            """);

        var snapshot = CodexAdapter.ParseQuota(Account, result, null);

        Assert.True(snapshot.Supported);
        Assert.Equal("plus", snapshot.Plan);
        Assert.Equal(AccountAvailability.Ready, snapshot.Availability);
        Assert.Equal(2, snapshot.Buckets.Count);
        var primary = snapshot.Buckets[0];
        Assert.Equal("5 часов", primary.Label);
        Assert.Equal(37, primary.UsedPercent);
        Assert.Equal(63, primary.RemainingPercent);
        Assert.Equal(300, primary.WindowMinutes);
        Assert.Equal(reset, primary.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.InRange(primary.ResetIn!.Value.TotalMinutes, 170, 181);
        Assert.Equal("Неделя", snapshot.Buckets[1].Label);
    }

    [Fact]
    public void Codex_reached_limit_marks_account_rate_limited()
    {
        var snapshot = CodexAdapter.ParseQuota(Account, Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1790000000},"rateLimitReachedType":"primary"}}
            """), "pro");

        Assert.Equal(AccountAvailability.RateLimited, snapshot.Availability);
        Assert.Equal("pro", snapshot.Plan);
    }

    [Fact]
    public void Codex_account_read_maps_identity_and_missing_login()
    {
        var ready = CodexAdapter.ToStatus(Json("""{"account":{"type":"chatgpt","email":"a@b.c","planType":"plus"},"requiresOpenaiAuth":true}"""), "0.160.0");
        Assert.Equal(AccountAvailability.Ready, ready.Availability);
        Assert.Equal("a@b.c", ready.Identity!.Email);
        Assert.Equal("plus", ready.Identity.Plan);

        var missing = CodexAdapter.ToStatus(Json("""{"account":null,"requiresOpenaiAuth":true}"""), null);
        Assert.Equal(AccountAvailability.AuthenticationRequired, missing.Availability);
    }

    [Fact]
    public void Cursor_uses_partial_deltas_and_skips_the_repeated_full_message()
    {
        const string sample = """
            {"type":"system","subtype":"init","apiKeySource":"login","model":"Auto"}
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"Reply with pong"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"po"}]},"timestamp_ms":1790994481166}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ng"}]},"timestamp_ms":1790994481170}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"pong","usage":{"inputTokens":4,"outputTokens":4,"cacheReadTokens":10,"cacheWriteTokens":17448}}
            """;
        var events = Run(new CursorAdapter.StreamJsonParser(), sample);

        Assert.Equal("pong", Text(events));
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.FinalText);
        var usage = Assert.Single(events, e => e.Kind == NativeChatEventKind.Usage).Usage!;
        Assert.Equal(4 + 10 + 17448, usage.PromptTokens);
        Assert.Equal(10, usage.CachedPromptTokens);
    }

    [Fact]
    public void Cursor_skips_buffered_copies_before_tool_calls()
    {
        var events = Run(new CursorAdapter.StreamJsonParser(), """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading. "}]},"timestamp_ms":1}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading. "}]},"timestamp_ms":2,"model_call_id":"mc_1"}
            {"type":"tool_call","subtype":"started","call_id":"t1"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]},"timestamp_ms":3}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading. Done."}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Reading. Done."}
            """);

        Assert.Equal("Reading. Done.", Text(events));
        Assert.DoesNotContain(events, e => e.Kind == NativeChatEventKind.FinalText);
    }

    [Fact]
    public void Codex_individual_spend_limit_is_reported()
    {
        var snapshot = CodexAdapter.ParseQuota(Account, Json("""
            {"rateLimits":{"limitId":"codex","individualLimit":{"limit":"25000","used":"8000","remainingPercent":68,"resetsAt":1790000000},"planType":"business"}}
            """), null);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(25000, bucket.Limit);
        Assert.Equal(17000, bucket.Remaining);
        Assert.Equal(32, bucket.UsedPercent);
        Assert.NotNull(bucket.ResetsAt);
    }

    [Fact]
    public void Cursor_without_partials_returns_final_text_and_errors()
    {
        var events = Run(new CursorAdapter.StreamJsonParser(), """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"pong"}
            """);
        Assert.Contains(events, e => e.Kind == NativeChatEventKind.FinalText && e.Text == "pong");

        var failed = Run(new CursorAdapter.StreamJsonParser(), """{"type":"result","subtype":"error","is_error":true,"result":"Authentication required. Please log in"}""");
        Assert.Equal(GatewayErrorKind.AuthenticationRequired, Assert.Single(failed).ErrorKind);
    }

    [Fact]
    public void Cursor_models_list_is_parsed()
    {
        var models = CursorAdapter.ParseModels("""
            Available models

            auto - Auto (current, default)
            gpt-5.5 - GPT-5.5
            claude-4.8-opus-thinking - Claude 4.8 Opus Thinking
            Tip: use --model <id>
            """);

        Assert.Equal(["auto", "gpt-5.5", "claude-4.8-opus-thinking"], models.Select(m => m.Id));
        Assert.True(models[0].IsDefault);
        Assert.Equal("Auto", models[0].DisplayName);
    }

    [Fact]
    public void Antigravity_stream_json_deltas_usage_and_status()
    {
        const string sample = """
            {"event":"init","conversation_id":"96c1","init":{"cwd":"D:\\w","tools":["view_file"],"permission_mode":"request-review"}}
            {"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"user_input"}}
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"pong"}}
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"\n"}}
            {"event":"result","result":{"status":"SUCCESS","response":"pong\n","num_turns":1,"usage":{"input_tokens":15618,"output_tokens":936,"thinking_tokens":935,"cache_read_tokens":0,"total_tokens":16554}}}
            """;
        var events = Run(new AntigravityAdapter.StreamJsonParser(), sample);

        Assert.Equal("pong\n", Text(events));
        var usage = Assert.Single(events, e => e.Kind == NativeChatEventKind.Usage).Usage!;
        Assert.Equal(16554, usage.TotalTokens);
        Assert.Equal(935, usage.ReasoningTokens);

        var failed = Run(new AntigravityAdapter.StreamJsonParser(), """{"event":"result","result":{"status":"ERROR","error":"RESOURCE_EXHAUSTED: quota exceeded"}}""");
        Assert.Equal(GatewayErrorKind.RateLimited, Assert.Single(failed).ErrorKind);
    }

    [Fact]
    public void Antigravity_models_are_parsed()
    {
        var models = AntigravityAdapter.ParseModels("gemini-3.5-pro\tGemini 3.5 Pro\ngemini-3.5-flash\tGemini 3.5 Flash\n\nSome footer text");
        Assert.Equal(["gemini-3.5-pro", "gemini-3.5-flash"], models.Select(m => m.Id));
        Assert.True(models[0].IsDefault);
    }

    [Fact]
    public void Grok_streaming_json_text_and_end_usage()
    {
        const string sample = """
            {"type":"available_commands","tools":["read_file"],"commands":["compact"]}
            {"type":"text","data":"po"}
            {"type":"text","data":"ng"}
            {"type":"usage","usage":{"input_tokens":14279,"output_tokens":28}}
            {"type":"end","stopReason":"end_turn","usage":{"input_tokens":14279,"cache_read_input_tokens":1664,"cache_creation_input_tokens":0,"output_tokens":28,"reasoning_tokens":27,"total_tokens":15971}}
            """;
        var events = Run(new GrokAdapter.StreamingJsonParser(), sample);

        Assert.Equal("pong", Text(events));
        var usage = Assert.Single(events, e => e.Kind == NativeChatEventKind.Usage).Usage!;
        Assert.Equal(14279 + 1664, usage.PromptTokens);
        Assert.Equal(28, usage.CompletionTokens);
        Assert.Equal(27, usage.ReasoningTokens);
    }

    [Fact]
    public void Grok_billing_maps_weekly_pool_and_reset()
    {
        var end = DateTimeOffset.UtcNow.AddDays(2);
        var start = end.AddDays(-7);
        var billing = Json($$$"""
            {"config":{"creditUsagePercent":43.0,
              "currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"{{{start:O}}}","end":"{{{end:O}}}"},
              "onDemandCap":{"val":1000},"onDemandUsed":{"val":250},"prepaidBalance":{"val":0}},
             "subscription_tier":"SuperGrok"}
            """);

        var snapshot = GrokAdapter.ParseBilling(new AccountProfile { Id = "grok-default", Provider = ProviderKind.Grok }, billing);

        Assert.Equal("SuperGrok", snapshot.Plan);
        var pool = snapshot.Buckets[0];
        Assert.Equal("Недельный пул", pool.Label);
        Assert.Equal(43.0, pool.UsedPercent);
        Assert.Equal(7 * 24 * 60, pool.WindowMinutes);
        Assert.Equal(end.ToUnixTimeSeconds(), pool.ResetsAt!.Value.ToUnixTimeSeconds());
        var onDemand = Assert.Single(snapshot.Buckets, b => b.Name == "on_demand");
        Assert.Equal(10, onDemand.Limit);
        Assert.Equal(7.5, onDemand.Remaining);
        Assert.DoesNotContain(snapshot.Buckets, b => b.Name == "prepaid");
    }

    [Fact]
    public void Grok_models_list_marks_default()
    {
        var models = GrokAdapter.ParseModels("You are logged in with grok.com.\n\n* grok-4.7 (default)\n- grok-4.6\n");
        Assert.Equal(["grok-4.7", "grok-4.6"], models.Select(m => m.Id));
        Assert.True(models[0].IsDefault);
        Assert.False(models[1].IsDefault);
    }

    [Fact]
    public void Antigravity_api_key_home_writes_its_own_settings_and_refuses_the_user_profile()
    {
        var home = Path.Combine(Path.GetTempPath(), "llmgw-agy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var resolved = AntigravityAdapter.ResolveApiKeyHome(new AccountProfile
            {
                Id = "agy-team",
                Provider = ProviderKind.Antigravity,
                AuthMode = AccountAuthMode.ApiKeyFromEnvironment,
                ConfigDirectory = home
            });
            Assert.Equal(Path.GetFullPath(home), resolved);
            AntigravityAdapter.EnsureApiKeySettings(resolved);
            var settings = File.ReadAllText(Path.Combine(resolved, ".gemini", "antigravity-cli", "settings.json"));
            Assert.Contains("\"modelProvider\": \"gemini\"", settings);

            var error = Assert.Throws<GatewayException>(() => AntigravityAdapter.ResolveApiKeyHome(new AccountProfile
            {
                Id = "agy-bad",
                Provider = ProviderKind.Antigravity,
                ConfigDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("Method '_x.ai/billing' not found")]
    [InlineData("account/rateLimits/read requires experimentalApi capability")]
    [InlineData("JSON-RPC error -32601")]
    public void Missing_rpc_methods_are_recognized(string message)
    {
        Assert.True(RpcErrors.IsMissingMethod(new GatewayException(GatewayErrorKind.Upstream, message)));
        Assert.False(RpcErrors.IsMissingMethod(new GatewayException(GatewayErrorKind.Upstream, "connection reset")));
    }
}
