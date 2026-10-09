using System.Net;
using LLMGateway.Core;
using LLMGateway.Core.Client;
namespace LLMGateway.Tests;
public sealed class RemoteExecutionBindingReviewTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("stream")]
    [InlineData("wire")]
    public async Task ExactInProcessBindingCannotBeSilentlyReroutedByTheHttpClient(string entry)
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var request = new ChatRequest
        {
            Model = "auto", Messages = [ChatMessage.User("owned binding fixture")],
            ExecutionContext = new(ProviderKind.Codex, "owned-account", "owned-model", Environment.CurrentDirectory)
        };
        var error = await Record.ExceptionAsync(async () =>
        {
            if (entry == "complete") await gateway.CompleteAsync(request);
            else if (entry == "stream") { await foreach (var item in gateway.StreamAsync(request)) { } }
            else OpenAiGatewayClient.ToWire(request, false);
        });
        Assert.Equal(GatewayErrorKind.Unsupported, Assert.IsType<GatewayException>(error).Kind);
        Assert.Equal(0, handler.Requests);
        Assert.NotNull(request.ExecutionContext);
        Assert.Equal("auto", request.Model);
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
