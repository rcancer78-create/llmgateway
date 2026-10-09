using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Tests;

public sealed class CustomHostRequestBodyReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicMapEnforcesItsOwnLimitForActualChunkedRequests(bool oversized)
    {
        using var directory = new TestDirectory();
        var adapter = new FakeAdapter();
        using var gateway = new LlmGateway(JsonAccountStore.InMemory(
            [new AccountProfile { Id = "owned", Provider = ProviderKind.Codex, IsActive = true }]), [adapter],
            new GatewayOptions { WorkspaceDirectory = directory.Root });
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = directory.Root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The custom host deliberately has no host cap; only MapLlmGateway's public option applies.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILlmGateway>(gateway);
        await using var app = builder.Build();
        var received = new ConcurrentQueue<(long? Length, bool Chunked)>();
        app.Use(async (context, next) =>
        {
            received.Enqueue((context.Request.ContentLength,
                context.Request.Headers.TransferEncoding.ToString().Contains("chunked", StringComparison.OrdinalIgnoreCase)));
            await next(context);
        });
        app.MapLlmGateway(new GatewayServerOptions { ApiKey = "synthetic-owned-key", MaxRequestBodyBytes = 1024 });
        await app.StartAsync();
        try
        {
            var url = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = "codex/owned/gpt-5.5",
                messages = new[] { new { role = "user", content = oversized ? new string('x', 2048) : "synthetic valid input" } }
            });
            Assert.Equal(oversized, body.Length > 1024);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ChunkedContent(body)
            };
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-owned-key");
            using var response = await http.SendAsync(request);
            var observation = Assert.Single(received);
            Assert.Null(observation.Length);
            Assert.True(observation.Chunked);

            Assert.Equal(oversized ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(oversized ? 0 : 1, adapter.Calls.Count);
            if (oversized) Assert.Contains("размер", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < body.Length; offset += 64)
                await stream.WriteAsync(body.AsMemory(offset, Math.Min(64, body.Length - offset))).ConfigureAwait(false);
        }
    }
}
