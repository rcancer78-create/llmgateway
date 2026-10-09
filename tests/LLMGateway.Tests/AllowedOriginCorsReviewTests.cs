using System.Net;
using System.Net.Http.Headers;
using LLMGateway.Core;
using LLMGateway.Server;

namespace LLMGateway.Tests;

public sealed class AllowedOriginCorsReviewTests
{
    private const string Origin = "https://owned-browser.invalid";

    [Theory]
    [InlineData("GET", "authorization")]
    [InlineData("POST", "authorization, content-type, x-llm-account")]
    [InlineData("PUT", "x-api-key, content-type")]
    [InlineData("DELETE", "authorization")]
    public async Task ApprovedPreflightNeedsNoApiKeyAndDoesNotDispatch(string method, string headers)
    {
        await Exercise(async (http, adapter) =>
        {
            using var request = Request(HttpMethod.Options, Origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", headers);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("Origin", response.Headers.Vary);
            Assert.Contains(method, response.Headers.GetValues("Access-Control-Allow-Methods").Single());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            Assert.Empty(adapter.Calls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedOriginActualRequestStillRequiresApiKey(bool authenticated)
    {
        await Exercise(async (http, adapter) =>
        {
            using var request = Request(HttpMethod.Get, Origin);
            if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owned-synthetic-api-key");
            using var response = await http.SendAsync(request);
            Assert.Equal(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("Origin", response.Headers.Vary);
            Assert.Empty(adapter.Calls);
        });
    }

    [Theory]
    [InlineData("https://other-owned.invalid", "GET", "authorization", true, false)]
    [InlineData(Origin, "GET", "authorization", false, false)]
    [InlineData(Origin, "TRACE", "authorization", true, false)]
    [InlineData(Origin, "GET", "x-unapproved-header", true, false)]
    [InlineData(Origin, "GET", "authorization", true, true)]
    public async Task RejectedPreflightNeverReceivesCorsPermission(string origin, string method, string headers, bool configured, bool badHost)
    {
        await Exercise(async (http, adapter) =>
        {
            using var request = Request(HttpMethod.Options, origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", headers);
            if (badHost) request.Headers.Host = "rebound-owned.invalid";
            using var response = await http.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.Empty(adapter.Calls);
        }, configured);
    }

    private static HttpRequestMessage Request(HttpMethod method, string origin)
    {
        var request = new HttpRequestMessage(method, "v1/models");
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static async Task Exercise(Func<HttpClient, FakeAdapter, Task> action, bool configured = true)
    {
        var root = Directory.CreateTempSubdirectory("llmgw-owned-cors-");
        try
        {
            var adapter = new FakeAdapter();
            using var gateway = new LlmGateway(JsonAccountStore.InMemory(
                [new() { Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex, IsActive = true }]),
                [adapter], new GatewayOptions { WorkspaceDirectory = root.FullName });
            await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
                new GatewayServerOptions { ApiKey = "owned-synthetic-api-key", LoopbackOnly = true, AllowedOrigins = configured ? [Origin] : [] });
            using var http = new HttpClient { BaseAddress = new Uri(server.Url + "/") };
            await action(http, adapter);
        }
        finally { root.Delete(true); }
    }
}
