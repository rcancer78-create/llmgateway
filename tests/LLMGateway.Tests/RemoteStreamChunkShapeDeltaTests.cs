using System.Net;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteStreamChunkShapeDeltaTests
{
    [Theory]
    [InlineData("{\"id\":\"owned\",\"model\":\"owned-model\",\"choices\":null}")]
    [InlineData("{\"id\":\"owned\",\"model\":\"owned-model\",\"choices\":[null]}")]
    [InlineData("{\"id\":\"owned\",\"model\":42,\"choices\":[]}")]
    public async Task MalformedChunkFieldsCannotEscapeTheTypedUpstreamBoundary(string data)
    {
        using var body = Body("data: " + data + "\n\n");
        using var http = Http(body);
        using var gateway = new OpenAiGatewayClient(http);
        var updates = new List<ChatUpdate>();

        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var update in gateway.StreamAsync(Request())) updates.Add(update);
        });

        var error = Assert.IsType<GatewayException>(failure);
        Assert.Equal(GatewayErrorKind.Upstream, error.Kind);
        Assert.DoesNotContain("choices", error.Message);
        Assert.DoesNotContain("owned-model", error.Message);
        Assert.DoesNotContain(updates, update => update.Kind == ChatUpdateKind.Completed);
        Assert.True(body.Disposed, "The malformed frame must finish the owned response-body lifetime.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageOnlyFramesAllowOmittedOrEmptyChoicesAndRetainTheCompletedReply(bool explicitEmptyChoices)
    {
        const string completed = "data: {\"id\":\"owned\",\"model\":\"owned-model\",\"choices\":[{\"delta\":{\"content\":\"owned reply\"},\"finish_reason\":\"stop\"}]}\n\n";
        // Canonical usage-only frames retain the stream identity while omitting choices.
        var usage = "data: {\"id\":\"owned\",\"model\":\"owned-model\"," + (explicitEmptyChoices ? "\"choices\":[]," : string.Empty)
            + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}\n\n";
        using var body = Body(completed + usage + "data: [DONE]\n\n");
        using var http = Http(body);
        using var gateway = new OpenAiGatewayClient(http);
        var updates = new List<ChatUpdate>();
        await foreach (var update in gateway.StreamAsync(Request())) updates.Add(update);

        Assert.Single(updates, update => update.Kind == ChatUpdateKind.Started);
        var result = Assert.Single(updates, update => update.Kind == ChatUpdateKind.Completed).Result!;
        Assert.Equal("owned reply", result.Content);
        Assert.Equal("owned-model", result.Model);
        Assert.Equal(1, result.Usage.PromptTokens);
        Assert.Equal(2, result.Usage.CompletionTokens);
        Assert.Equal(3, result.Usage.TotalTokens);
        Assert.True(body.Disposed);
    }

    private static ChatRequest Request() => new() { Model = "owned-model", Messages = [ChatMessage.User("owned fixture")] };
    private static ObservedBody Body(string events) => new(Encoding.UTF8.GetBytes(events));
    private static HttpClient Http(Stream body) => new(new Handler(body)) { BaseAddress = new Uri("https://owned-gateway.invalid/") };

    private sealed class Handler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(body) });
    }

    private sealed class ObservedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
