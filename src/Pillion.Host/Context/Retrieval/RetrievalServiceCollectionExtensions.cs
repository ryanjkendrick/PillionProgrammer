using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.ModelGateway;

namespace Pillion.Host.Context.Retrieval;

/// <summary>Dependency injection registration for hybrid retrieval (work item 2.11).</summary>
internal static class RetrievalServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRetrievalService"/>, the gateway-backed <see cref="IQueryEmbedder"/> and, unless one is already
    /// registered, the read-only <see cref="IIndexCatalog"/>. The indexing pipeline (work item 2.9) registers its live
    /// catalog first to take its place. Needs <see cref="PillionPaths"/>, <see cref="IFeatureFlags"/> and
    /// <see cref="IModelGateway"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionRetrieval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIndexCatalog>(sp => new ReadOnlyIndexCatalog(
            sp.GetRequiredService<PillionPaths>(),
            sp.GetRequiredService<IFeatureFlags>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IQueryEmbedder>(sp => new GatewayQueryEmbedder(sp.GetRequiredService<IModelGateway>()));
        services.TryAddSingleton<IRetrievalService>(sp => new RetrievalService(
            sp.GetRequiredService<IIndexCatalog>(),
            sp.GetRequiredService<IFeatureFlags>(),
            sp.GetService<IQueryEmbedder>(),
            sp.GetService<RetrievalOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<RetrievalService>>()));
        return services;
    }
}
