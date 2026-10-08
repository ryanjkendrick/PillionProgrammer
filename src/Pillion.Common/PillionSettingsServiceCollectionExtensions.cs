using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;

namespace Pillion.Common;

/// <summary>Dependency injection registration for settings and secrets.</summary>
public static class PillionSettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PillionPaths"/>, <see cref="SettingsStore"/>, the Windows Credential Manager
    /// <see cref="ISecretStore"/> and the default <see cref="IProviderTester"/>. Registrations that already exist for
    /// <see cref="ISecretStore"/> and <see cref="IProviderTester"/> are kept, so a later or earlier registration can replace them.
    /// Any <see cref="ISettingsMigration"/> services in the container are passed to the store.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">The data layout to use, or <see langword="null"/> for <c>%LOCALAPPDATA%\PillionProgrammer</c>.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionSettings(this IServiceCollection services, PillionPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (paths is not null)
        {
            services.AddSingleton(paths);
        }
        else
        {
            services.TryAddSingleton(provider => new PillionPaths(root: null, provider.GetService<ILogger<PillionPaths>>()));
        }

        services.TryAddSingleton(provider => new SettingsStore(
            provider.GetRequiredService<PillionPaths>(),
            new SettingsStoreOptions { Migrations = provider.GetServices<ISettingsMigration>().ToList() },
            provider.GetService<ILogger<SettingsStore>>()));

        services.TryAddSingleton<ISecretStore, WindowsCredentialManagerSecretStore>();
        services.TryAddSingleton<IProviderTester, NoProviderTester>();
        return services;
    }
}
