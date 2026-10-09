using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Server;

namespace LLMGateway.Tests;

public sealed class StandaloneWireReviewRegressionTests
{
    [Theory]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":42}}", true)]
    [InlineData("{\"type\":\"function\",\"function\":42}", true)]
    [InlineData("\"none\"", false)]
    public async Task MalformedToolChoiceIsA400BeforeNativeDispatch(string choice, bool malformed)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-wire-");
        try
        {
            var adapter = new FakeAdapter();
            var store = JsonAccountStore.InMemory([new() { Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }],
                Path.Combine(root.FullName, "accounts.json"));
            using var gateway = new LlmGateway(store, [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "owned-fixture" });
            using var http = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owned-fixture");
            var body = "{\"model\":\"codex/owned/gpt-5.5\",\"messages\":[{\"role\":\"user\",\"content\":\"owned fixture\"}],\"tool_choice\":" + choice + "}";
            using var response = await http.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(malformed ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
            if (malformed) Assert.Empty(adapter.Calls);
            else Assert.Single(adapter.Calls);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("-f=owned")]
    [InlineData("-fowned")]
    public void CompactBannedForceFlagsAreRejectedBeforeLaunch(string argument) =>
        Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(CliArguments.Parse(argument)));
}
