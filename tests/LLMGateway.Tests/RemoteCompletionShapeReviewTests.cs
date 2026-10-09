using System.Net;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteCompletionShapeReviewTests
{
    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\"}]}")]
    [InlineData("{\"choices\":null}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\" \",\"message\":{\"content\":\"answer\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"call1\",\"function\":{\"arguments\":\"{}\"}}]}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"call1\",\"function\":{\"name\":\"known\"}}]}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[null]}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":\"answer\"}}]}")]
    public async Task MalformedHttp200CannotManufactureSuccessfulCompletion(string payload)
    {
        using var http = new HttpClient(new ResponseHandler(payload)) { BaseAddress = new Uri("https://gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(Request()));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
    }

    [Theory]
    [InlineData("stop", "")]
    [InlineData("stop", "answer")]
    [InlineData("length", "partial")]
    public async Task ExplicitTerminalMessagePreservesLegitimateEmptyOrPartialContent(string finish, string content)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content } } } });
        using var http = new HttpClient(new ResponseHandler(payload)) { BaseAddress = new Uri("https://gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var result = await gateway.CompleteAsync(Request());
        Assert.Equal(finish, result.FinishReason);
        Assert.Equal(content, result.Content);
    }

    [Fact]
    public async Task CompleteToolCallPreservesActualFields()
    {
        const string payload = """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"call1","type":"function","function":{"name":"known","arguments":"{\"value\":1}"}}]}}]}""";
        using var http = new HttpClient(new ResponseHandler(payload)) { BaseAddress = new Uri("https://gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var result = await gateway.CompleteAsync(Request());
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call1", call.Id);
        Assert.Equal("known", call.Name);
        Assert.Equal("{\"value\":1}", call.ArgumentsJson);
        Assert.Equal("tool_calls", result.FinishReason);
    }

    private static ChatRequest Request() => new() { Model = "remote", Messages = [ChatMessage.User("shape fixture")] };

    private sealed class ResponseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }
}
