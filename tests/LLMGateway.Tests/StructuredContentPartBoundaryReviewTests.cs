using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Server;

namespace LLMGateway.Tests;

public sealed class StructuredContentPartBoundaryReviewTests
{
    [Theory]
    [InlineData("{\"type\":42,\"text\":\"owned\"}")]
    [InlineData("{\"type\":\"text\",\"text\":42}")]
    [InlineData("{\"type\":\"refusal\",\"refusal\":42}")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public Task MalformedStructuredContentIsA400BeforeAdapterDispatch(string part) => Exercise(part, true);

    [Fact]
    public Task ValidTextContentPartsStillReachTheOwnedAdapter() =>
        Exercise("{\"type\":\"text\",\"text\":\"owned text\"}", false);

    private static async Task Exercise(string part, bool malformed)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-content-boundary-");
        try
        {
            var adapter = new FakeAdapter();
            var store = JsonAccountStore.InMemory([new() { Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }],
                Path.Combine(root.FullName, "accounts.json"));
            using var gateway = new LlmGateway(store, [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "owned-fixture" });
            using var http = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owned-fixture");
            var body = "{\"model\":\"codex/owned/gpt-5.5\",\"messages\":[{\"role\":\"user\",\"content\":[" + part + "]}]}";
            using var response = await http.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(malformed ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
            if (malformed) Assert.Empty(adapter.Calls);
            else Assert.Single(adapter.Calls);
        }
        finally { root.Delete(true); }
    }
}
