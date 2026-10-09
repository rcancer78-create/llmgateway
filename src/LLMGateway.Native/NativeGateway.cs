using LLMGateway.Core;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Native;

public static class NativeGateway
{
    /// <summary>Creates a ready gateway without a DI container (library mode).</summary>
    public static LlmGateway Create(GatewayOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        options ??= new GatewayOptions();
        loggerFactory ??= NullLoggerFactory.Instance;
        var adapters = CreateAdapters(options, loggerFactory);
        var store = JsonAccountStore.Load(options, adapters, loggerFactory.CreateLogger<JsonAccountStore>());
        return new LlmGateway(store, adapters, options, loggerFactory.CreateLogger<LlmGateway>());
    }

    public static IReadOnlyList<IProviderAdapter> CreateAdapters(GatewayOptions options, ILoggerFactory loggerFactory)
    {
        var resolver = new ExecutableResolver();
        return
        [
            new CodexAdapter(resolver, options, loggerFactory.CreateLogger<CodexAdapter>()),
            new CursorAdapter(resolver, options, loggerFactory.CreateLogger<CursorAdapter>()),
            new AntigravityAdapter(resolver, options, loggerFactory.CreateLogger<AntigravityAdapter>()),
            new GrokAdapter(resolver, options, loggerFactory.CreateLogger<GrokAdapter>()),
            new ClaudeAdapter(resolver, options, loggerFactory.CreateLogger<ClaudeAdapter>())
        ];
    }

    /// <summary>Registers <see cref="ILlmGateway"/> with all native adapters.</summary>
    public static IServiceCollection AddLlmGateway(this IServiceCollection services, Action<GatewayOptions>? configure = null)
    {
        var options = new GatewayOptions();
        configure?.Invoke(options);
        return services.AddLlmGateway(options);
    }

    public static IServiceCollection AddLlmGateway(this IServiceCollection services, GatewayOptions options)
    {
        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.TryAddSingleton(options);
        services.TryAddSingleton<ExecutableResolver>();
        services.AddSingleton<IProviderAdapter, CodexAdapter>();
        services.AddSingleton<IProviderAdapter, CursorAdapter>();
        services.AddSingleton<IProviderAdapter, AntigravityAdapter>();
        services.AddSingleton<IProviderAdapter, GrokAdapter>();
        services.AddSingleton<IProviderAdapter, ClaudeAdapter>();
        services.TryAddSingleton<IAccountStore>(sp => JsonAccountStore.Load(
            sp.GetRequiredService<GatewayOptions>(),
            sp.GetServices<IProviderAdapter>(),
            sp.GetRequiredService<ILogger<JsonAccountStore>>()));
        services.TryAddSingleton<LlmGateway>(sp => new LlmGateway(
            sp.GetRequiredService<IAccountStore>(),
            sp.GetServices<IProviderAdapter>(),
            sp.GetRequiredService<GatewayOptions>(),
            sp.GetRequiredService<ILogger<LlmGateway>>()));
        services.TryAddSingleton<ILlmGateway>(sp => sp.GetRequiredService<LlmGateway>());
        return services;
    }
}
