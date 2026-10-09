using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class SdkClientBoundaryReviewRegressionTests
{
    [Fact]
    public async Task BorrowedHttpClientKeepsItsPriorAuthorizationAndDoesNotLeakTheGatewayKeyToAnotherHost()
    {
        var handler = new Handler("{\"object\":\"list\",\"data\":[]}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        var original = new AuthenticationHeaderValue("Basic", "owned-prior-value");
        http.DefaultRequestHeaders.Authorization = original;
        using (var gateway = new OpenAiGatewayClient(http, "owned-gateway-value"))
            Assert.Empty(await gateway.GetAccountsAsync());
        using var unrelated = await http.GetAsync("https://owned-other.invalid/plain");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("owned-gateway.invalid", handler.Requests[0].Host);
        Assert.Equal("Bearer", handler.Requests[0].Scheme);
        Assert.Equal("owned-gateway-value", handler.Requests[0].Value);
        Assert.Equal("owned-other.invalid", handler.Requests[1].Host);
        Assert.Equal("Basic", handler.Requests[1].Scheme);
        Assert.Equal("owned-prior-value", handler.Requests[1].Value);
        Assert.Same(original, http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false, "sk-owned-fixture", true)]
    [InlineData(true, "sk-owned-fixture", true)]
    [InlineData(false, "Bearer owned-fixture", true)]
    [InlineData(true, "Bearer owned-fixture", true)]
    [InlineData(false, "ordinary-setting", false)]
    [InlineData(true, "ordinary-setting", false)]
    public async Task RemoteAccountWritesRejectCredentialShapesBeforeAnyHttpRequest(bool update, string value, bool reject)
    {
        var info = new AccountInfo("owned", "Owned", ProviderKind.Codex, true, true, AccountAvailability.Unknown,
            null, "fixture", null, AccountAuthMode.NativeLogin, null, null, null, null, null);
        var handler = new Handler(JsonSerializer.Serialize(info, GatewayJson.Options));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http, "owned-access");
        var profile = new AccountProfile
        {
            Id = "owned", DisplayName = "Owned", Provider = ProviderKind.Codex,
            Environment = new Dictionary<string, string> { ["NOTE"] = value }
        };
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (update) await gateway.UpdateAccountAsync(profile);
            else await gateway.AddAccountAsync(profile);
        });
        Assert.Equal(reject ? 0 : 1, handler.Requests.Count);
        if (reject) Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.IsType<GatewayException>(failure).Kind);
        else Assert.Null(failure);
        Assert.Equal(value, profile.Environment["NOTE"]); // Refusal does not silently alter the caller's draft.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandardSseDataFramingPreservesTheCompletedReply(bool multiline)
    {
        const string prefix = "{\"id\":\"chatcmpl-owned\",\"model\":\"owned-model\",";
        const string suffix = "\"created\":1,\"choices\":[{\"index\":0,\"delta\":{\"content\":\"owned reply\"},\"finish_reason\":\"stop\"}]}";
        var response = ": owned keep-alive\n\n" + (multiline
            ? "data: " + prefix + "\ndata: " + suffix
            : "data: " + prefix + suffix) + "\n\ndata: [DONE]\n\n";
        var handler = new Handler(response, "text/event-stream");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var updates = new List<ChatUpdate>();
        await foreach (var update in gateway.StreamAsync(new() { Model = "owned-model", Messages = [ChatMessage.User("owned fixture")] }))
            updates.Add(update);
        Assert.Equal(ChatUpdateKind.Started, updates[0].Kind);
        Assert.Equal(ChatUpdateKind.Completed, updates[^1].Kind);
        Assert.Single(updates, update => update.Kind == ChatUpdateKind.Completed);
        Assert.Equal("owned reply", updates[^1].Result!.Content);
        Assert.Equal("owned reply", string.Concat(updates.Where(update => update.Kind == ChatUpdateKind.TextDelta).Select(update => update.Text)));
    }

    private sealed class Handler(string body, string mediaType = "application/json") : HttpMessageHandler
    {
        public List<(string Host, string? Scheme, string? Value)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }
}
