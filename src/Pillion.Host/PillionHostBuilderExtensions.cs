using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Connectivity;
using Pillion.Host.Lifecycle;
using Pillion.Host.Logging;
using Pillion.Host.Models;
using Pillion.Host.Rpc;
using Pillion.Host.Settings;
using Pillion.ModelGateway;

namespace Pillion.Host;

/// <summary>Options for the running host, from the command line.</summary>
/// <param name="PipeName">The named pipe the host serves RPC on.</param>
/// <param name="ParentProcessId">The process that launched the host; the host exits when it exits.</param>
/// <param name="LogDirectory">The log folder.</param>
internal sealed record PillionHostOptions(string PipeName, int ParentProcessId, string LogDirectory);

/// <summary>Composes Pillion Host. Used by <c>Program</c> and by the in-process tests.</summary>
internal static class PillionHostBuilderExtensions
{
    /// <summary>The host's overall shutdown budget; the RPC drain itself is capped at one second.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Adds logging, the lifecycle services and the RPC server. Later work items add their RPC targets with
    /// <see cref="RpcTargetServiceCollectionExtensions.AddRpcTarget{TContract, TImplementation}(IServiceCollection)"/>.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="options">The host options.</param>
    /// <param name="tokenSource">Supplies the launch token.</param>
    /// <returns>The same builder.</returns>
    public static HostApplicationBuilder AddPillionHost(
        this HostApplicationBuilder builder,
        PillionHostOptions options,
        ILaunchTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenSource);

        builder.Logging.AddPillionHostLogging(options.LogDirectory);

        builder.Services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = ShutdownTimeout;
            o.ServicesStartConcurrently = true;
            o.ServicesStopConcurrently = true;
        });
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(tokenSource);
        builder.Services.AddPillionLifecycle();
        builder.Services.AddPillionRpcServer();

        // Settings (work item 0.5): the store, Credential Manager secrets and the ISettingsService RPC target.
        builder.Services.AddPillionSettingsRpc();
        builder.Services.AddRpcTarget<ISettingsService>(sp => sp.GetRequiredService<ISettingsService>());

        // Connectivity (work item 0.10): the monitor, its probes and the connectivityChanged notification.
        builder.Services.AddPillionConnectivity();

        // The gateway reads the same effective settings as the monitor (one source of truth for Local-only), asks the
        // monitor which providers are reachable, and reports circuit-breaker evidence back to it. Registered before
        // AddPillionModelGateway, whose TryAdd defaults (AlwaysReachable, NoProviderHealthSink) then stay out.
        builder.Services.TryAddSingleton<IGatewaySettingsSource>(sp =>
            new ConnectivityGatewaySettingsSource(sp.GetRequiredService<IConnectivitySettingsProvider>()));
        builder.Services.TryAddSingleton<IProviderReachability>(sp =>
            new ConnectivityProviderReachability(sp.GetRequiredService<IConnectivityMonitor>()));
        builder.Services.TryAddSingleton<IProviderHealthSink>(sp =>
            new ConnectivityProviderHealthSink(sp.GetRequiredService<IConnectivityMonitor>()));

        // Feature flags: the effective settings (user settings plus repository policies) say which features are on.
        // The gateway RPC service and the connectivity monitor honour them; the VSIX reads them through the
        // effective.features virtual key of ISettingsService.
        builder.Services.TryAddSingleton<IFeatureFlags>(sp =>
            new SettingsFeatureFlags(sp.GetRequiredService<IConnectivitySettingsProvider>()));

        // Model Gateway (work items 0.7 and 0.8): providers, routing and the IModelService RPC target. It also
        // replaces the settings' NoProviderTester with one that probes the named provider (Test connection).
        builder.Services.AddPillionModelGateway();
        builder.Services.AddSingleton<ModelRpcService>();
        builder.Services.AddRpcTarget<IModelService>(sp => sp.GetRequiredService<ModelRpcService>());

        // RPC targets for the context and agent services are registered here by later work items.
        return builder;
    }
}
