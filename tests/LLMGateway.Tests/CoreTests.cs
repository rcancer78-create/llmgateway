using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;

namespace LLMGateway.Tests;

public class ModelRouterTests
{
    private static readonly List<AccountProfile> Accounts =
    [
        new() { Id = "codex-default", Provider = ProviderKind.Codex, IsActive = true },
        new() { Id = "codex-work", Provider = ProviderKind.Codex, DefaultModel = "gpt-5.5" },
        new() { Id = "grok-default", Provider = ProviderKind.Grok, IsActive = true },
        new() { Id = "claude-default", Provider = ProviderKind.Claude, IsActive = true, Enabled = false }
    ];

    private static RouteResult Route(string model, string? account = null, Func<string, IReadOnlyList<NativeModel>?>? models = null) =>
        ModelRouter.Resolve(new ChatRequest { Model = model, AccountId = account }, Accounts, ProviderKind.Codex, models ?? (_ => null));

    [Fact]
    public void Full_id_selects_account_and_model()
    {
        var route = Route("codex/codex-work/gpt-5.4-mini");
        Assert.Equal("codex-work", route.Account.Id);
        Assert.Equal("gpt-5.4-mini", route.NativeModel);
    }

    [Fact]
    public void Provider_prefix_uses_active_account()
    {
        var route = Route("codex/gpt-5.5");
        Assert.Equal("codex-default", route.Account.Id);
        Assert.Equal("gpt-5.5", route.NativeModel);
        Assert.Null(Route("grok").NativeModel);
    }

    [Fact]
    public void Account_field_overrides_and_default_model_applies()
    {
        var route = Route("auto", "codex-work");
        Assert.Equal("codex-work", route.Account.Id);
        Assert.Equal("gpt-5.5", route.NativeModel);
        Assert.Equal("grok-4.7", Route("grok-4.7", "grok-default").NativeModel);
    }

    [Fact]
    public void Bare_model_is_found_in_cached_models()
    {
        var route = Route("grok-4.7", models: id => id == "grok-default" ? [new NativeModel("grok-4.7", "Grok 4.7")] : []);
        Assert.Equal("grok-default", route.Account.Id);
    }

    [Fact]
    public void Provider_prefix_skips_a_disabled_active_account()
    {
        List<AccountProfile> accounts =
        [
            new() { Id = "codex-off", Provider = ProviderKind.Codex, IsActive = true, Enabled = false },
            new() { Id = "codex-on", Provider = ProviderKind.Codex }
        ];
        var route = ModelRouter.Resolve(new ChatRequest { Model = "codex/gpt-5.5" }, accounts, ProviderKind.Codex, _ => null);
        Assert.Equal("codex-on", route.Account.Id);
        Assert.Equal("gpt-5.5", route.NativeModel);
    }

    [Theory]
    [InlineData("codex/gpt-5.5", "grok-default", GatewayErrorKind.InvalidRequest)]
    [InlineData("auto", "missing", GatewayErrorKind.NotFound)]
    [InlineData("unknown-model", null, GatewayErrorKind.ModelNotFound)]
    [InlineData("claude", null, GatewayErrorKind.ProviderUnavailable)]
    [InlineData("--dangerous", null, GatewayErrorKind.InvalidRequest)]
    [InlineData("codex/a b", null, GatewayErrorKind.InvalidRequest)]
    public void Invalid_routes_fail_with_typed_errors(string model, string? account, GatewayErrorKind kind)
    {
        var error = Assert.Throws<GatewayException>(() => Route(model, account));
        Assert.Equal(kind, error.Kind);
    }
}

public class PromptAndToolTests
{
    [Fact]
    public void Single_user_message_is_passed_as_is()
    {
        var prompt = PromptBuilder.Build(new ChatRequest { Messages = [ChatMessage.User("Привет")] }, 1000);
        Assert.Equal("Привет", prompt);
    }

    [Fact]
    public void Leading_dash_is_never_passed_raw()
    {
        var prompt = PromptBuilder.Build(new ChatRequest { Messages = [ChatMessage.User("--help")] }, 1000);
        Assert.StartsWith("# Conversation", prompt);
    }

    [Fact]
    public void Transcript_contains_instructions_tools_and_results()
    {
        var request = new ChatRequest
        {
            Messages =
            [
                ChatMessage.System("Be brief."),
                ChatMessage.User("time?"),
                new ChatMessage(ChatRole.Assistant, string.Empty) { ToolCalls = [new ToolCall("call_1", "get_time", "{\"utc\":true}")] },
                new ChatMessage(ChatRole.Tool, "{\"now\":\"12:00\"}") { ToolCallId = "call_1", Name = "get_time" }
            ],
            Tools = [new ToolDefinition("get_time", "Current time", "{\"type\":\"object\"}")]
        };

        var prompt = PromptBuilder.Build(request, 100_000);

        Assert.StartsWith("# Instructions\nBe brief.", prompt);
        Assert.Contains("- get_time: Current time", prompt);
        Assert.Contains("<tool_calls>[{\"id\":\"call_1\",\"name\":\"get_time\",\"arguments\":{\"utc\":true}}]</tool_calls>", prompt);
        Assert.Contains("## Tool result (tool_call_id=call_1) [get_time]\n{\"now\":\"12:00\"}", prompt);
    }

    [Fact]
    public void Oldest_turns_are_dropped_to_fit_the_limit()
    {
        var request = new ChatRequest
        {
            Messages = [ChatMessage.User(new string('a', 500)), ChatMessage.Assistant(new string('b', 500)), ChatMessage.User("last question")]
        };

        var prompt = PromptBuilder.Build(request, 300);

        Assert.True(prompt.Length <= 300);
        Assert.Contains("last question", prompt);
        Assert.Contains("omitted", prompt);
        Assert.DoesNotContain("aaaa", prompt);
        Assert.Equal(GatewayErrorKind.InvalidRequest,
            Assert.Throws<GatewayException>(() => PromptBuilder.Build(new ChatRequest { Messages = [ChatMessage.User(new string('x', 400))] }, 300)).Kind);
    }

    [Fact]
    public void Tool_calls_are_parsed_only_for_known_tools()
    {
        ToolDefinition[] tools = [new("get_time", null, null)];
        var (text, calls) = ToolCalling.Parse("Sure.\n<tool_calls>[{\"name\":\"get_time\",\"arguments\":{\"utc\":true}}]</tool_calls>", tools);
        var call = Assert.Single(calls);
        Assert.Equal("get_time", call.Name);
        Assert.Equal("{\"utc\":true}", call.ArgumentsJson);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal("Sure.", text);
        var error = Assert.Throws<GatewayException>(() =>
            ToolCalling.Parse("<tool_calls>[{\"name\":\"rm_rf\"}]</tool_calls>", tools));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
    }

    [Fact]
    public void Fenced_tool_block_is_accepted()
    {
        var (_, calls) = ToolCalling.Parse("<tool_calls>\n```json\n{\"name\":\"get_time\",\"arguments\":\"{}\"}\n```\n</tool_calls>", [new ToolDefinition("get_time", null, null)]);
        Assert.Equal("{}", Assert.Single(calls).ArgumentsJson);
    }

    [Fact]
    public void Stop_filter_does_not_leak_partial_markers()
    {
        var filter = new StopSequenceFilter(["END"]);
        var output = filter.Push("Hello E") + filter.Push("N") + filter.Push("D tail");
        Assert.Equal("Hello ", output + filter.Flush());
        Assert.True(filter.Stopped);

        var passthrough = new StopSequenceFilter(["END"]);
        Assert.Equal("Hello EN", passthrough.Push("Hello EN") + passthrough.Flush());
    }

    [Fact]
    public void Json_fences_are_stripped()
    {
        Assert.Equal("{\"a\":1}", PromptBuilder.StripJsonFences("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", PromptBuilder.StripJsonFences(" {\"a\":1} "));
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("You've hit your usage limit.", GatewayErrorKind.RateLimited)]
    [InlineData("429 Too Many Requests", GatewayErrorKind.RateLimited)]
    [InlineData("RESOURCE_EXHAUSTED", GatewayErrorKind.RateLimited)]
    [InlineData("Not logged in. Run `codex login`", GatewayErrorKind.AuthenticationRequired)]
    [InlineData("401 Unauthorized", GatewayErrorKind.AuthenticationRequired)]
    [InlineData("socket closed", GatewayErrorKind.Upstream)]
    public void Messages_are_classified(string message, GatewayErrorKind kind) =>
        Assert.Equal(kind, NativeErrorClassifier.Classify(message));

    [Fact]
    public void Retry_time_is_parsed()
    {
        var now = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddHours(2).AddMinutes(5), NativeErrorClassifier.ParseRetryAt("Try again in 2h 5m.", now));
        Assert.Equal(now.AddDays(1).AddHours(3), NativeErrorClassifier.ParseRetryAt("limit resets in 1 day and 3 hours", now));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), NativeErrorClassifier.ParseRetryAt("resets at 2026-10-04T00:00:00Z", now));
        Assert.Null(NativeErrorClassifier.ParseRetryAt("try later", now));
    }
}

public class AccountStoreTests
{
    [Fact]
    public async Task Legacy_array_is_migrated_without_losing_ids()
    {
        var directory = Directory.CreateTempSubdirectory("llmgw-store-");
        try
        {
            var file = Path.Combine(directory.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, """
                [
                  {"id":"codex-main","displayName":"Main","provider":"Codex","executable":"codex","environment":{"CODEX_HOME":"C:\\codex-main","FOO":"1"},"isActive":true},
                  {"id":"codex-work","displayName":"Work","provider":0,"executable":"C:\\tools\\codex.cmd","environment":{"CODEX_HOME":"C:\\codex-work"}}
                ]
                """);

            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            var all = store.GetAll();

            var main = Assert.Single(all, a => a.Id == "codex-main");
            Assert.Equal("C:\\codex-main", main.ConfigDirectory);
            Assert.Null(main.Executable);
            Assert.Equal("1", main.Environment["FOO"]);
            Assert.True(main.IsActive);
            Assert.Equal("C:\\tools\\codex.cmd", Assert.Single(all, a => a.Id == "codex-work").Executable);
            foreach (var provider in Enum.GetValues<ProviderKind>().Where(provider => provider != ProviderKind.Unknown))
                Assert.Single(all, a => a.Provider == provider && a.IsActive);

            var reloaded = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []).GetAll();
            Assert.Equal(all.Select(a => a.Id).Order(), reloaded.Select(a => a.Id).Order());
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal(JsonValueKind.Object, saved.RootElement.ValueKind);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Select_keeps_one_active_account_per_provider_and_validates()
    {
        var store = JsonAccountStore.InMemory(
        [
            new AccountProfile { Id = "codex-a", Provider = ProviderKind.Codex, IsActive = true },
            new AccountProfile { Id = "codex-b", Provider = ProviderKind.Codex }
        ]);
        try
        {
            await store.SelectAsync("codex-b");
            Assert.Equal("codex-b", Assert.Single(store.GetAll(), a => a.IsActive).Id);

            await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(new AccountProfile { Id = "codex-a", Provider = ProviderKind.Codex }));
            await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(new AccountProfile { Id = "../x", Provider = ProviderKind.Codex }));
            await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(new AccountProfile { Id = "codex-c", Provider = ProviderKind.Codex, ApiKeyVariable = "BAD NAME" }));
        }
        finally
        {
            File.Delete(store.FilePath);
        }
    }

    [Fact]
    public async Task Secret_environment_values_are_rejected_and_removed_on_load()
    {
        var directory = Directory.CreateTempSubdirectory("llmgw-store-");
        try
        {
            var file = Path.Combine(directory.FullName, "accounts.json");
            await File.WriteAllTextAsync(file, """
                {"version":2,"accounts":[{"id":"codex-main","display_name":"Main","provider":"codex","environment":{"FOO":"1","GEMINI_API_KEY":"sk-live","TOKENIZER_PATH":"C:\\tools"}}]}
                """);
            var store = JsonAccountStore.Load(new GatewayOptions { AccountsFile = file, DiscoverProfiles = false }, []);
            var main = store.Find("codex-main")!;
            Assert.Equal("1", main.Environment["FOO"]);
            Assert.Equal(@"C:\tools", main.Environment["TOKENIZER_PATH"]);
            Assert.False(main.Environment.ContainsKey("GEMINI_API_KEY"));
            var saved = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("sk-live", saved);
            Assert.DoesNotContain("GEMINI_API_KEY", saved);
            var error = await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(new AccountProfile
            {
                Id = "codex-secret",
                Provider = ProviderKind.Codex,
                Environment = new Dictionary<string, string> { ["NOTE"] = "sk-test" }
            }));
            Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}

public class OpenAiMapperTests
{
    private static OpenAiChatRequest Parse(string json) => JsonSerializer.Deserialize<OpenAiChatRequest>(json, GatewayJson.Options)!;

    [Fact]
    public void Content_arrays_roles_and_options_are_mapped()
    {
        var request = OpenAiMapper.ToChatRequest(Parse("""
            {"model":"codex/codex-work/gpt-5.5","messages":[
              {"role":"developer","content":[{"type":"text","text":"Rules"}]},
              {"role":"user","content":[{"type":"text","text":"Hello"},{"type":"text","text":"world"}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"f","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"42"}],
             "stop":"END","reasoning_effort":"high","response_format":{"type":"json_object"},
             "tools":[{"type":"function","function":{"name":"f","description":"d","parameters":{"type":"object"}}}],
             "tool_choice":{"type":"function","function":{"name":"f"}},"max_completion_tokens":100}
            """), accountHeader: "codex-work");

        Assert.Equal("codex-work", request.AccountId);
        Assert.Equal(ChatRole.Developer, request.Messages[0].Role);
        Assert.Equal("Rules", request.Messages[0].Content);
        Assert.Equal("Hello\nworld", request.Messages[1].Content);
        Assert.Equal("call_1", Assert.Single(request.Messages[2].ToolCalls!).Id);
        Assert.Equal("call_1", request.Messages[3].ToolCallId);
        Assert.Equal(["END"], request.Stop!);
        Assert.Equal("high", request.ReasoningEffort);
        Assert.Equal(ResponseFormatKind.JsonObject, request.ResponseFormat!.Kind);
        Assert.Equal("f", request.ToolChoice);
        Assert.Equal("{\"type\":\"object\"}", Assert.Single(request.Tools!).ParametersJson);
        Assert.Equal(100, request.MaxOutputTokens);
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"http://x"}}]}]}""", GatewayErrorKind.Unsupported)]
    [InlineData("""{"model":"m","n":2,"messages":[{"role":"user","content":"hi"}]}""", GatewayErrorKind.Unsupported)]
    [InlineData("""{"model":"m","max_tokens":10,"max_completion_tokens":10,"messages":[{"role":"user","content":"hi"}]}""", GatewayErrorKind.InvalidRequest)]
    [InlineData("""{"model":"m","n":0,"messages":[{"role":"user","content":"hi"}]}""", GatewayErrorKind.InvalidRequest)]
    [InlineData("""{"model":"m","logprobs":true,"messages":[{"role":"user","content":"hi"}]}""", GatewayErrorKind.Unsupported)]
    [InlineData("""{"model":"m","max_tokens":0,"messages":[{"role":"user","content":"hi"}]}""", GatewayErrorKind.InvalidRequest)]
    [InlineData("""{"model":"m","messages":[{"role":"wizard","content":"hi"}]}""", GatewayErrorKind.InvalidRequest)]
    [InlineData("""{"model":"m","messages":[]}""", GatewayErrorKind.InvalidRequest)]
    public void Unsupported_inputs_fail_explicitly(string json, GatewayErrorKind kind)
    {
        var error = Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(Parse(json)));
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public void Malformed_stop_and_tool_types_are_rejected()
    {
        var stop = Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(Parse(
            """{"model":"m","stop":["ok",3],"messages":[{"role":"user","content":"hi"}]}""")));
        Assert.Equal(GatewayErrorKind.InvalidRequest, stop.Kind);

        var tool = Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(Parse(
            """{"model":"m","tools":[{"type":"computer","function":{"name":"f"}}],"messages":[{"role":"user","content":"hi"}]}""")));
        Assert.Equal(GatewayErrorKind.Unsupported, tool.Kind);
    }

    [Theory]
    [InlineData(GatewayErrorKind.InvalidRequest, 400)]
    [InlineData(GatewayErrorKind.Unauthorized, 401)]
    [InlineData(GatewayErrorKind.ModelNotFound, 404)]
    [InlineData(GatewayErrorKind.RateLimited, 429)]
    [InlineData(GatewayErrorKind.ProviderUnavailable, 503)]
    public void Error_kinds_map_to_http_statuses(GatewayErrorKind kind, int status)
    {
        var described = OpenAiMapper.Describe(kind);
        Assert.Equal(status, described.Status);
        Assert.Equal(kind, OpenAiMapper.KindFromWire(described.Status, described.Code));
    }
}

public class CliArgumentTests
{
    [Fact]
    public void Quoted_arguments_round_trip()
    {
        var parsed = CliArguments.Parse("--model \"gpt 5\" --note \"say \\\"hi\\\"\"");
        Assert.Equal(["--model", "gpt 5", "--note", "say \"hi\""], parsed);
        Assert.Equal("--model \"gpt 5\" --note \"say \\\"hi\\\"\"", CliArguments.Format(parsed));
    }

    [Theory]
    [InlineData("--yolo")]
    [InlineData("--force")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("--sandbox danger-full-access")]
    [InlineData("--sandbox=disabled")]
    [InlineData("--permission-mode bypassPermissions")]
    [InlineData("-a on-failure")]
    public void Permission_bypasses_are_rejected(string text)
    {
        var error = Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(CliArguments.Parse(text)));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public void Read_only_sandbox_flag_is_allowed()
    {
        CliArguments.RejectUnsafe(CliArguments.Parse("--sandbox read-only --color never"));
    }
}
