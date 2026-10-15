using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.LocalStore.Conversations;

namespace Pillion.LocalStore;

/// <summary>Dependency injection registration for the Local Store.</summary>
public static class LocalStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation store (<see cref="IConversationStore"/>, opened lazily on first use and closed when the
    /// container is disposed) and its <see cref="ConversationRetention"/>. Needs <see cref="PillionPaths"/> and
    /// <see cref="IFeatureFlags"/>. Existing registrations are kept.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionLocalStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConversationStore>(provider => new ConversationStore(
            provider.GetRequiredService<PillionPaths>(),
            provider.GetRequiredService<IFeatureFlags>(),
            provider.GetService<ILoggerFactory>()));
        services.TryAddSingleton(provider => new ConversationRetention(
            provider.GetRequiredService<IConversationStore>(),
            provider.GetService<ConversationRetentionPolicy>(),
            provider.GetService<TimeProvider>()));
        return services;
    }
}
