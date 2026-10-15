using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Contracts;

namespace Pillion.Host.Rpc;

/// <summary>
/// An RPC target the server exposes to the VSIX on every authenticated connection: an implementation of one of the
/// host-side contract interfaces (for example <see cref="IModelService"/>). Register one with
/// <see cref="RpcTargetServiceCollectionExtensions.AddRpcTarget{TContract, TImplementation}(IServiceCollection)"/>.
/// </summary>
internal interface IRpcTarget
{
    /// <summary>Gets the contract interface whose methods are exposed (CLR method names, no transform).</summary>
    Type ContractType { get; }

    /// <summary>Gets the object that implements <see cref="ContractType"/>. Called once per connection.</summary>
    /// <param name="services">The host's service provider.</param>
    /// <returns>The target object.</returns>
    object GetTarget(IServiceProvider services);
}

/// <summary>
/// A notification the server accepts from the VSIX on every authenticated connection (client to host, no response), for
/// example <c>ideActivityChanged</c>. Register one with
/// <see cref="RpcTargetServiceCollectionExtensions.AddRpcNotification"/>; like requests, it is only dispatched after the
/// handshake has been accepted.
/// </summary>
internal interface IRpcNotificationTarget
{
    /// <summary>Gets the notification (method) name.</summary>
    string Name { get; }

    /// <summary>Gets the handler; its parameters are the notification's positional arguments. Called once per connection.</summary>
    /// <param name="services">The host's service provider.</param>
    /// <returns>The handler.</returns>
    Delegate GetHandler(IServiceProvider services);
}

/// <summary>An <see cref="IRpcNotificationTarget"/> resolved through a factory.</summary>
/// <param name="name">The notification name.</param>
/// <param name="factory">Returns the handler; called once per connection.</param>
internal sealed class RpcNotificationRegistration(string name, Func<IServiceProvider, Delegate> factory) : IRpcNotificationTarget
{
    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public Delegate GetHandler(IServiceProvider services) => factory(services);
}

/// <summary>An <see cref="IRpcTarget"/> resolved through a factory.</summary>
/// <param name="contractType">The contract interface.</param>
/// <param name="factory">Resolves the implementation; called once per connection.</param>
internal sealed class RpcTargetRegistration(Type contractType, Func<IServiceProvider, object> factory) : IRpcTarget
{
    /// <inheritdoc/>
    public Type ContractType { get; } = contractType;

    /// <inheritdoc/>
    public object GetTarget(IServiceProvider services) => factory(services);
}

/// <summary>Registers RPC targets.</summary>
internal static class RpcTargetServiceCollectionExtensions
{
    /// <summary>
    /// Exposes <typeparamref name="TContract"/> to the VSIX, implemented by a singleton
    /// <typeparamref name="TImplementation"/>. Calls are only dispatched after the connection's handshake has been
    /// accepted, and errors are mapped as described in the host README.
    /// </summary>
    /// <typeparam name="TContract">A public contract interface from Pillion.Contracts (or a test interface).</typeparam>
    /// <typeparam name="TImplementation">The implementation, registered as a singleton if not already registered.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddRpcTarget<TContract, TImplementation>(this IServiceCollection services)
        where TContract : class
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TImplementation>();
        return services.AddRpcTarget<TContract>(sp => sp.GetRequiredService<TImplementation>());
    }

    /// <summary>Exposes <typeparamref name="TContract"/> to the VSIX, implemented by the object a factory returns.</summary>
    /// <typeparam name="TContract">A public contract interface.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Returns the implementation; called once per connection.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TContract"/> is not an interface, or is reserved.</exception>
    /// <exception cref="InvalidOperationException">A target for <typeparamref name="TContract"/> is already registered.</exception>
    public static IServiceCollection AddRpcTarget<TContract>(this IServiceCollection services, Func<IServiceProvider, TContract> factory)
        where TContract : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        var contract = typeof(TContract);
        if (!contract.IsInterface)
        {
            throw new ArgumentException($"RPC target contract {contract.Name} must be an interface.", nameof(TContract));
        }

        if (contract == typeof(IHostLifecycleService))
        {
            throw new ArgumentException($"{nameof(IHostLifecycleService)} is served by the RPC server itself.", nameof(TContract));
        }

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IRpcTarget) &&
                descriptor.ImplementationInstance is IRpcTarget existing &&
                existing.ContractType == contract)
            {
                throw new InvalidOperationException($"An RPC target for {contract.Name} is already registered.");
            }
        }

        services.AddSingleton<IRpcTarget>(new RpcTargetRegistration(contract, sp => factory(sp)));
        return services;
    }

    /// <summary>Accepts a client-to-host notification on every authenticated connection.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The notification name.</param>
    /// <param name="factory">Returns the handler (its parameters are the positional arguments); called once per connection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddRpcNotification(this IServiceCollection services, string name, Func<IServiceProvider, Delegate> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton<IRpcNotificationTarget>(new RpcNotificationRegistration(name, factory));
        return services;
    }
}
