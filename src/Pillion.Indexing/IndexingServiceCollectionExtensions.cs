using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;

namespace Pillion.Indexing;

/// <summary>Dependency injection registration for repository indexing.</summary>
public static class IndexingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IndexStoreFactory"/> and <see cref="IndexRetention"/>. Needs <see cref="PillionPaths"/> and
    /// <see cref="IFeatureFlags"/>; an <see cref="IndexRetentionPolicy"/> from the <c>indexing.retentionDays</c> setting,
    /// an <see cref="IProcessProbe"/> or a <see cref="TimeProvider"/> are used if registered. Existing registrations are kept.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionIndexing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new IndexStoreFactory(
            provider.GetRequiredService<PillionPaths>(),
            provider.GetRequiredService<IFeatureFlags>(),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IProcessProbe>(),
            provider.GetService<IndexStoreOptions>()));
        services.TryAddSingleton(provider => new IndexRetention(
            provider.GetRequiredService<PillionPaths>(),
            provider.GetRequiredService<IFeatureFlags>(),
            provider.GetService<IndexRetentionPolicy>(),
            provider.GetService<IProcessProbe>(),
            provider.GetService<TimeProvider>()));
        return services;
    }
}
