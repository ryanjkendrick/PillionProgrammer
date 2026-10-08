using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;

namespace Pillion.Host.Connectivity;

/// <summary>Dependency injection registration for connectivity detection.</summary>
public static class ConnectivityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ConnectivityMonitor"/> (also as <see cref="IConnectivityMonitor"/>), the HTTP prober, the
    /// system network-change source, the repository policy registry, and the hosted service that runs the monitor and
    /// sends the <c>connectivityChanged</c> notification. Requires the RPC server (<c>AddPillionRpcServer</c>), which
    /// <c>AddPillionHost</c> adds. Settings services are added with <see cref="PillionSettingsServiceCollectionExtensions.AddPillionSettings"/>
    /// when missing. Every service is registered with <c>TryAdd</c>, so a test or a later registration can replace it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPillionConnectivity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPillionSettings();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ConnectivityMonitorOptions>();
        services.TryAddSingleton<RepositoryPolicyRegistry>();
        services.TryAddSingleton<IRepositoryPolicyProvider>(sp => sp.GetRequiredService<RepositoryPolicyRegistry>());
        services.TryAddSingleton<IConnectivitySettingsProvider>(sp =>
            new StoreConnectivitySettingsProvider(
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<IRepositoryPolicyProvider>()));
        services.TryAddSingleton<IProviderProber>(_ => new HttpProviderProber());
        services.TryAddSingleton<INetworkChangeSource, SystemNetworkChangeSource>();
        services.TryAddSingleton<ConnectivityMonitor>();
        services.TryAddSingleton<IConnectivityMonitor>(sp => sp.GetRequiredService<ConnectivityMonitor>());
        services.TryAddSingleton<INotificationSender, VsixNotificationSender>();
        services.AddHostedService<ConnectivityHostedService>();
        return services;
    }
}
