using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;

namespace LLMGateway.Tests;

public sealed class StructuredErrorStatusReviewTests
{
    [Theory]
    [InlineData("{\"status\":\"RESOURCE_EXHAUSTED\"}", GatewayErrorKind.RateLimited)]
    [InlineData("{\"status\":\"UNAUTHENTICATED\"}", GatewayErrorKind.AuthenticationRequired)]
    [InlineData("{\"status\":\"RESOURCE_EXHAUSTED\",\"code\":429}", GatewayErrorKind.RateLimited)]
    [InlineData("{\"status_code\":null,\"http_status\":429}", GatewayErrorKind.RateLimited)]
    [InlineData("{\"status\":{},\"http_status\":403}", GatewayErrorKind.AuthenticationRequired)]
    [InlineData("{\"status\":[],\"http_status\":500}", GatewayErrorKind.Upstream)]
    [InlineData("{\"status\":false}", GatewayErrorKind.Upstream)]
    public void NonNumericStatusDoesNotDiscardStructuredError(string json, GatewayErrorKind expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, NativeErrorClassifier.Classify(document.RootElement, "native failure"));
    }

    [Fact]
    public void GrokEndRetainsUsageAndErrorAfterAnEarlierDelta()
    {
        var parser = new GrokAdapter.StreamingJsonParser();
        using var delta = JsonDocument.Parse("""{"type":"text","data":"partial"}""");
        Assert.Equal("partial", Assert.Single(parser.Parse(delta.RootElement)).Text);
        using var end = JsonDocument.Parse("""{"type":"end","stopReason":"error","usage":{"input_tokens":7,"output_tokens":2},"error":{"status":"RESOURCE_EXHAUSTED","code":429}}""");
        var events = parser.Parse(end.RootElement).ToList();
        Assert.Equal(7, Assert.Single(events, e => e.Kind == NativeChatEventKind.Usage).Usage!.PromptTokens);
        Assert.Equal(GatewayErrorKind.RateLimited, Assert.Single(events, e => e.Kind == NativeChatEventKind.Error).ErrorKind);
    }
}
