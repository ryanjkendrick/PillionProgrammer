using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Contracts;

namespace Pillion.Host.Settings;

/// <summary>Dependency injection registration for the settings RPC service.</summary>
public static class SettingsRpcServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SettingsRpcService"/> as the host's <see cref="ISettingsService"/>, together with
    /// everything it depends on (see <see cref="PillionSettingsServiceCollectionExtensions.AddPillionSettings"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">The data layout to use, or <see langword="null"/> for the per-user default.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionSettingsRpc(this IServiceCollection services, PillionPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPillionSettings(paths);
        services.TryAddSingleton<SettingsRpcService>();
        services.TryAddSingleton<ISettingsService>(provider => provider.GetRequiredService<SettingsRpcService>());
        return services;
    }
}
