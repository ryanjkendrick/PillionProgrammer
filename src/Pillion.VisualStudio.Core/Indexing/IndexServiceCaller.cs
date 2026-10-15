using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>The outcome of one call to the host's <see cref="IContextService"/>: a value, or why there is none.</summary>
/// <typeparam name="T">The value type.</typeparam>
internal readonly struct IndexCall<T>
{
    public IndexCall(T? value, RpcFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    /// <summary>Gets the value; meaningful only when <see cref="Succeeded"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets why the call failed, or <see langword="null"/> when it succeeded.</summary>
    public RpcFailure? Failure { get; }

    /// <summary>Gets a value indicating whether the call succeeded.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Calls <see cref="IContextService"/> for the Index window's view models: always off the UI thread, with a timeout, and
/// never throwing (except when the window closes). The caller switches back to the main thread afterwards.
/// </summary>
internal sealed class IndexServiceCaller
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly IHostConnection _connection;
    private readonly PillionLog? _log;
    private readonly CancellationToken _disposal;

    public IndexServiceCaller(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log, CancellationToken disposal)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        JoinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _log = log;
        _disposal = disposal;
    }

    /// <summary>Gets the package's joinable task factory.</summary>
    public JoinableTaskFactory JoinableTaskFactory { get; }

    /// <summary>Gets the token that is cancelled when the window closes.</summary>
    public CancellationToken Disposal => _disposal;

    /// <summary>Gets a value indicating whether the host is connected and handshaken.</summary>
    public bool IsHostReady => _connection.State is HostState.Ready or HostState.Degraded;

    /// <summary>Calls the host on the thread pool.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="name">What is called, for the log.</param>
    /// <param name="call">The call.</param>
    /// <returns>The result or the failure. Continues on the thread pool: switch to the main thread before touching a view model.</returns>
    public async Task<IndexCall<T>> CallAsync<T>(string name, Func<IContextService, CancellationToken, Task<T>> call)
    {
        await TaskScheduler.Default;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal);
            timeout.CancelAfter(CallTimeout);
            var value = await call(_connection.GetProxy<IContextService>(), timeout.Token).ConfigureAwait(false);
            return new IndexCall<T>(value, failure: null);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure is shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = RpcFailure.Describe(ex);
            _log?.Debug("index.window.call.failed", "A call for the Index window failed.", ("call", name), ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
            return new IndexCall<T>(default, failure);
        }
    }
}
