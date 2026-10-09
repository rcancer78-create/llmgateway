using LLMGateway.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Server;

/// <summary>Hosts the OpenAI-compatible API inside another process (chat app, LLMWorkGUI).</summary>
public sealed class EmbeddedGatewayServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EmbeddedGatewayServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<EmbeddedGatewayServer> StartAsync(
        ILlmGateway gateway,
        string url = "http://127.0.0.1:5157",
        GatewayServerOptions? options = null,
        GatewayOptions? gatewayOptions = null,
        Action<ILoggingBuilder>? logging = null,
        bool preserveLoggingProviders = false,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = (options ?? new GatewayServerOptions()).MaxRequestBodyBytes);
        if (!preserveLoggingProviders) builder.Logging.ClearProviders();
        logging?.Invoke(builder.Logging);
        builder.Services.AddSingleton(gateway);
        builder.Services.AddSingleton(options ?? new GatewayServerOptions());
        if (gatewayOptions is not null) builder.Services.AddSingleton(gatewayOptions);
        var app = builder.Build();
        app.MapLlmGateway();
        try { await app.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? url;
        return new EmbeddedGatewayServer(app, bound);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync().ConfigureAwait(false); }
        finally { await _app.DisposeAsync().ConfigureAwait(false); }
    }
}
