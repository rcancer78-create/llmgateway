using System.Net;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.Client;
namespace LLMGateway.Tests;
public sealed class SdkRefusalPreservationReviewTests
{
    [Theory]
    [InlineData(null, "owned-refusal", "owned-refusal")]
    [InlineData("owned-content", "owned-refusal", "owned-contentowned-refusal")]
    [InlineData("owned-content", null, "owned-content")]
    [InlineData("owned-content", "", "owned-content")]
    [InlineData("", "owned-refusal", "owned-refusal")]
    public async Task BufferedContentAndRefusalAreBothPreserved(string? content, string? refusal, string expected)
    {
        var message = new { role = "assistant", content, refusal };
        using var http = OwnedHttp(Completion(message));
        using var client = new OpenAiGatewayClient(http);
        Assert.Equal(expected, (await client.CompleteAsync(Request())).Content);
    }

    [Theory]
    [InlineData(null, "owned-refusal", "owned-refusal")]
    [InlineData("owned-content", "owned-refusal", "owned-contentowned-refusal")]
    [InlineData("owned-content", null, "owned-content")]
    [InlineData("owned-content", "", "owned-content")]
    [InlineData("", "owned-refusal", "owned-refusal")]
    public async Task StreamedContentAndRefusalAreBothVisibleAndPreserved(string? content, string? refusal, string expected)
    {
        var updates = await ReadStream(Chunk(new { content, refusal }, "stop") + "data: [DONE]\n\n");
        Assert.Equal(expected, updates[^1].Result!.Content);
        Assert.Equal(expected, string.Concat(updates.Where(u => u.Kind == ChatUpdateKind.TextDelta).Select(u => u.Text)));
        Assert.Single(updates, u => u.Kind == ChatUpdateKind.Completed);
    }

    [Fact]
    public async Task RefusalFragmentsAreNotLostOrSeparatedByInventedWhitespace()
    {
        var updates = await ReadStream(Chunk(new { refusal = "owned-" }) + Chunk(new { refusal = "refusal" }, "stop") + "data: [DONE]\n\n");
        Assert.Equal("owned-refusal", updates[^1].Result!.Content);
        Assert.Equal("owned-refusal", string.Concat(updates.Where(u => u.Kind == ChatUpdateKind.TextDelta).Select(u => u.Text)));
    }

    [Fact]
    public async Task BufferedRefusalDoesNotDiscardValidatedToolCalls()
    {
        var calls = new[] { new { id = "owned-call", type = "function", function = new { name = "owned_tool", arguments = "{}" } } };
        using var http = OwnedHttp(Completion(new { role = "assistant", content = (string?)null, refusal = "owned-refusal", tool_calls = calls }, "tool_calls"));
        using var client = new OpenAiGatewayClient(http);
        var result = await client.CompleteAsync(Request());
        Assert.Equal("owned-refusal", result.Content);
        Assert.Equal(new ToolCall("owned-call", "owned_tool", "{}"), Assert.Single(result.ToolCalls));
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Fact]
    public async Task StreamedRefusalDoesNotDiscardValidatedToolCalls()
    {
        var calls = new[] { new { index = 0, id = "owned-call", type = "function", function = new { name = "owned_tool", arguments = "{}" } } };
        var updates = await ReadStream(Chunk(new { refusal = "owned-refusal", tool_calls = calls }, "tool_calls") + "data: [DONE]\n\n");
        var result = updates[^1].Result!;
        Assert.Equal("owned-refusal", result.Content);
        Assert.Equal(new ToolCall("owned-call", "owned_tool", "{}"), Assert.Single(result.ToolCalls));
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public async Task NonTextRefusalCannotBeSilentlyDroppedFromStream(string value)
    {
        var body = "data: {\"id\":\"owned\",\"model\":\"owned\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"owned\",\"refusal\":" + value + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var error = await Assert.ThrowsAsync<GatewayException>(async () => await ReadStream(body));
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
    }

    private static ChatRequest Request() => new() { Model = "owned", Messages = [ChatMessage.User("owned fixture")] };
    private static string Completion(object message, string finish = "stop") => JsonSerializer.Serialize(new { id = "owned", created = 1, model = "owned", choices = new[] { new { index = 0, message, finish_reason = finish } } });
    private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { id = "owned", created = 1, model = "owned", choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";
    private static HttpClient OwnedHttp(string body, string mediaType = "application/json") => new(new Handler(body, mediaType)) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
    private static async Task<List<ChatUpdate>> ReadStream(string body)
    {
        using var http = OwnedHttp(body, "text/event-stream");
        using var client = new OpenAiGatewayClient(http);
        var updates = new List<ChatUpdate>();
        await foreach (var update in client.StreamAsync(Request())) updates.Add(update);
        return updates;
    }
    private sealed class Handler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}
