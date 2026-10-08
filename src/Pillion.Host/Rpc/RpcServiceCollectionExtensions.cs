using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pillion.Host.Rpc;

/// <summary>Registers the RPC server.</summary>
internal static class RpcServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <see cref="RpcServer"/> hosted service, <see cref="IVsixConnection"/> and the native pipe client
    /// identification. Requires <see cref="PillionHostOptions"/>, an <see cref="ILaunchTokenSource"/> and the
    /// lifecycle services. RPC targets are added separately with
    /// <see cref="RpcTargetServiceCollectionExtensions.AddRpcTarget{TContract, TImplementation}(IServiceCollection)"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPillionRpcServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RpcServerOptions>();
        services.TryAddSingleton<IPipeClientProcessIdProvider, NativePipeClientProcessIdProvider>();
        services.TryAddSingleton<VsixConnection>();
        services.TryAddSingleton<IVsixConnection>(sp => sp.GetRequiredService<VsixConnection>());
        services.AddHostedService<RpcServer>();
        return services;
    }
}
