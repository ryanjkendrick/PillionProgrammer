using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pillion.Host.Lifecycle;

/// <summary>Registers the host lifecycle services.</summary>
internal static class LifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="HostStateMachine"/>, <see cref="HostShutdown"/> and the <see cref="ParentProcessWatcher"/>.
    /// Requires <see cref="PillionHostOptions"/> to be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPillionLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<HostStateMachine>();
        services.TryAddSingleton<HostShutdown>();
        services.AddHostedService<ParentProcessWatcher>();
        return services;
    }
}
