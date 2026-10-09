using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Providers.OpenAICompatible;

namespace Pillion.ModelGateway;

/// <summary>An <see cref="IProviderTester"/> that probes the named provider through the gateway (Test connection).</summary>
public sealed class GatewayProviderTester : IProviderTester
{
    private readonly IModelGateway _gateway;

    /// <summary>Initializes a new instance of the <see cref="GatewayProviderTester"/> class.</summary>
    /// <param name="gateway">The gateway.</param>
    public GatewayProviderTester(IModelGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <inheritdoc/>
    public Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken) =>
        _gateway.TestProviderAsync(providerId, cancellationToken);
}

/// <summary>Dependency injection registration for the Model Gateway.</summary>
public static class ModelGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Model Gateway: the catalog, prompt templates, provider registry (with the Ollama, OpenAI-compatible, OpenAI, Anthropic and Gemini factories), router,
    /// circuit breakers, concurrency limiter, autocomplete cache, Outbound log and <see cref="IModelGateway"/>. The
    /// default <see cref="IProviderTester"/> is replaced by <see cref="GatewayProviderTester"/>. Extension points that
    /// later work items supply (<see cref="IProviderReachability"/>, <see cref="IProviderHealthSink"/>,
    /// <see cref="IProgressSink"/>, <see cref="ICostGovernor"/>, <see cref="IGatewaySettingsSource"/>) are only added
    /// if nothing is registered yet. Requires <see cref="SettingsStore"/> (unless an <see cref="IGatewaySettingsSource"/>
    /// is registered) and an <see cref="ISecretStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts the gateway options.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionModelGateway(this IServiceCollection services, Action<ModelGatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ModelGatewayOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ModelCatalog.Default);
        services.TryAddSingleton(PromptTemplates.Default);

        services.TryAddSingleton<IProviderReachability, AlwaysReachable>();
        services.TryAddSingleton<IProviderHealthSink, NoProviderHealthSink>();
        services.TryAddSingleton<IProgressSink, NoProgressSink>();
        services.TryAddSingleton<ICostGovernor, NoCostCaps>();
        services.TryAddSingleton<IGatewaySettingsSource>(sp => new SettingsStoreGatewaySettingsSource(sp.GetRequiredService<SettingsStore>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderFactory, OllamaProviderFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderFactory, OpenAICompatibleProviderFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderFactory, OpenAIProviderFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderFactory, AnthropicProviderFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderFactory, GeminiProviderFactory>());
        services.TryAddSingleton(sp => new ProviderRegistry(
            sp.GetRequiredService<IGatewaySettingsSource>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetServices<IModelProviderFactory>(),
            sp.GetRequiredService<ModelCatalog>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IProviderRegistry>(sp => sp.GetRequiredService<ProviderRegistry>());

        services.TryAddSingleton<ModelInventory>();
        services.TryAddSingleton(sp => new CircuitBreakerRegistry(
            sp.GetRequiredService<ModelGatewayOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IProviderHealthSink>(),
            sp.GetService<ILogger<CircuitBreakerRegistry>>()));
        services.TryAddSingleton<ConcurrencyLimiter>();
        services.TryAddSingleton<AutocompleteCache>();
        services.TryAddSingleton<IOutboundLog>(sp => new InMemoryOutboundLog(
            sp.GetRequiredService<ModelGatewayOptions>(),
            sp.GetService<ILogger<InMemoryOutboundLog>>()));
        services.TryAddSingleton<IModelRouter, ModelRouter>();
        services.TryAddSingleton<IModelGateway>(sp => new ModelGateway(
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<IModelRouter>(),
            sp.GetRequiredService<ModelInventory>(),
            sp.GetRequiredService<ModelCatalog>(),
            sp.GetRequiredService<PromptTemplates>(),
            sp.GetRequiredService<AutocompleteCache>(),
            sp.GetRequiredService<CircuitBreakerRegistry>(),
            sp.GetRequiredService<ConcurrencyLimiter>(),
            sp.GetRequiredService<IOutboundLog>(),
            sp.GetRequiredService<IProviderReachability>(),
            sp.GetRequiredService<ICostGovernor>(),
            sp.GetRequiredService<IProgressSink>(),
            sp.GetRequiredService<ModelGatewayOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<ModelGateway>>()));

        // Test connection probes the named provider through the gateway (replacing the settings' NoProviderTester).
        services.Replace(ServiceDescriptor.Singleton<IProviderTester, GatewayProviderTester>());
        return services;
    }
}
