using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pillion.Common;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Connectivity;
using Pillion.Host.Context;
using Pillion.Host.Conversations;
using Pillion.Host.Lifecycle;
using Pillion.Host.Logging;
using Pillion.Host.Models;
using Pillion.Host.Rpc;
using Pillion.Host.Settings;
using Pillion.LocalStore;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;

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

        // The settingsChanged notification (work item 1.15): which top-level settings sections changed.
        builder.Services.AddPillionSettingsNotifications();

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
        // The bundled embedding models (work item 2.7) are downloaded into the same data root as the settings, and their
        // files are verified once in the background when the host starts.
        builder.Services.TryAddSingleton(sp => new BundledModelOptions
        {
            UserModelsDirectory = sp.GetService<PillionPaths>()?.ModelsDirectory ?? BundledModelOptions.DefaultUserModelsDirectory(),
        });
        builder.Services.AddPillionModelGateway();
        builder.Services.AddHostedService<BundledModelWarmup>();
        builder.Services.AddSingleton<ModelRpcService>();
        builder.Services.AddRpcTarget<IModelService>(sp => sp.GetRequiredService<ModelRpcService>());
        builder.Services.AddPillionInlineEdit(); // Inline edits (work item 1.8): IInlineEditService over the gateway.
        builder.Services.AddRpcTarget<IDiagnosticsService, DiagnosticsRpcService>(); // Work item 1.5: read-only autocomplete counters.
        builder.Services.TryAddSingleton<ContextPreview>(); // Work item 1.13: the Context Inspector's preview behind IDiagnosticsService.

        // Mentions and context (work item 1.10): IContextService resolves @-mentions; search and indexing arrive in phase 2.
        // The indexStatusChanged notification (contract 1.3), throttled to twice a second; the pipeline (2.9) publishes to it.
        builder.Services.AddRpcTarget<IContextService, ContextRpcService>();
        builder.Services.TryAddSingleton<INotificationSender, VsixNotificationSender>();
        builder.Services.TryAddSingleton<IndexStatusNotifier>();

        // Local Store (work item 2.1): the encrypted conversations.db, opened lazily behind the localStore flag. Work item
        // 2.2 persists conversations and the Outbound log through it, behind IConversationService (contract 1.3, a stub
        // until then).
        builder.Services.AddPillionLocalStore();
        builder.Services.AddRpcTarget<IConversationService, ConversationRpcService>();

        // Persistence (work item 2.2): chat requests that name a conversation are saved as they run, and the Outbound log's
        // records are written to the store as they are produced (the gateway's in-memory log feeds the sink), so the
        // conversation list and the Outbound log view survive a restart. The sink is also a hosted service, so it drains
        // its queue when the host stops.
        builder.Services.TryAddSingleton<ConversationRecorder>();
        builder.Services.TryAddSingleton<StoreOutboundSink>();
        builder.Services.AddSingleton<IOutboundSink>(sp => sp.GetRequiredService<StoreOutboundSink>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StoreOutboundSink>());
        builder.Services.AddHostedService<ConversationRetentionService>();

        // RPC targets for the agent service are registered here by a later work item.
        return builder;
    }
}
