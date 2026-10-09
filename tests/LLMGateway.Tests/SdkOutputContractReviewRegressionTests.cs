using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;

namespace LLMGateway.Tests;

public sealed class SdkOutputContractReviewRegressionTests
{
    [Fact]
    public async Task PlainCompletedTextExactlyMatchesTheEmittedWhitespace()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-plain-output-");
        try
        {
            const string text = "\n    owned indentation\n\n";
            var adapter = new OutputAdapter(text);
            using var gateway = Create(root, adapter);
            var updates = new List<ChatUpdate>();
            await foreach (var update in gateway.StreamAsync(Request())) updates.Add(update);
            Assert.Equal(text, string.Concat(updates.Where(update => update.Kind == ChatUpdateKind.TextDelta).Select(update => update.Text)));
            Assert.Equal(text, updates[^1].Result!.Content);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("owned prose", false)]
    [InlineData("[1,2]", false)]
    [InlineData("{\"owned\":true}", true)]
    public async Task JsonObjectOnlyCompletesWithAnActualValidObject(string output, bool valid)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-json-output-");
        try
        {
            using var gateway = Create(root, new OutputAdapter(output));
            var request = Request();
            request.ResponseFormat = new(ResponseFormatKind.JsonObject);
            if (valid)
            {
                var result = await gateway.CompleteAsync(request);
                using var document = JsonDocument.Parse(result.Content!);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(request));
                Assert.Equal(GatewayErrorKind.Upstream, failure.Kind);
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SchemaModeIsExplicitlyUnsupportedBeforeNativeDispatchUntilFullValidationExists()
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-schema-output-");
        try
        {
            var adapter = new OutputAdapter("{\"owned\":true}");
            using var gateway = Create(root, adapter);
            var request = Request();
            request.ResponseFormat = new(ResponseFormatKind.JsonSchema, "owned-schema", "{\"type\":\"object\"}");
            var failure = await Record.ExceptionAsync(() => gateway.CompleteAsync(request));
            Assert.Equal(0, adapter.Calls);
            Assert.Equal(GatewayErrorKind.Unsupported, Assert.IsType<GatewayException>(failure).Kind);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void CapabilityMetadataExplicitlyDeclaresSchemaValidationUnavailable()
    {
        var capabilities = new OutputAdapter("owned").Capabilities;
        var metadata = JsonSerializer.SerializeToElement(capabilities, GatewayJson.Options);
        Assert.True(metadata.TryGetProperty("supports_json_schema", out var schemaSupport));
        Assert.False(schemaSupport.GetBoolean());
    }

    [Theory]
    [InlineData("{\"type\":\"json_schema\"}")]
    [InlineData("{\"type\":\"json_schema\",\"json_schema\":{\"schema\":{}}}")]
    [InlineData("{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"owned\",\"schema\":[]}}")]
    public void MalformedSchemaRequestsAreInvalidRatherThanReplacedWithAnEmptySchema(string format)
    {
        var request = JsonSerializer.Deserialize<OpenAiChatRequest>(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"owned\"}],\"response_format\":" + format + "}", GatewayJson.Options)!;
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(request)).Kind);
    }

    private static ChatRequest Request() => new() { Model = "codex/owned/model", Messages = [ChatMessage.User("owned fixture")] };
    private static LlmGateway Create(DirectoryInfo root, OutputAdapter adapter) => new(
        JsonAccountStore.InMemory([new() { Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }],
            Path.Combine(root.FullName, "accounts.json")), [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });

    private sealed class OutputAdapter(string text) : IProviderAdapter
    {
        public int Calls { get; private set; }
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fake";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(false, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100_000);
        public string? ResolveExecutable(AccountProfile account) => "owned-fake";
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NativeModel>>([new("model", "Owned model")]);
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            await Task.CompletedTask;
            yield return NativeChatEvent.Delta(text);
        }
    }
}
