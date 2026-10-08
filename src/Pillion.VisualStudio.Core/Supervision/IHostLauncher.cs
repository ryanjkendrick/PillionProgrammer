using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Starts Pillion Host and returns a handshaken connection to it.</summary>
public interface IHostLauncher
{
    /// <summary>
    /// Launches the host process, connects to its pipe and performs the handshake.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the launch (the process is killed).</param>
    /// <returns>The live session.</returns>
    /// <exception cref="HostLaunchException">The launch failed; <see cref="HostLaunchException.IsRetryable"/> says whether to back off and retry.</exception>
    Task<IHostSession> LaunchAsync(CancellationToken cancellationToken);
}

/// <summary>One running host process with a handshaken RPC connection.</summary>
public interface IHostSession : IDisposable
{
    /// <summary>Gets the host process id.</summary>
    int ProcessId { get; }

    /// <summary>Gets the latest state the host reported (from <c>hostStateChanged</c> or <c>GetStateAsync</c>).</summary>
    HostState LatestHostState { get; }

    /// <summary>Raised on a background thread when the host sends <c>hostStateChanged</c>.</summary>
    event EventHandler<HostState>? HostStateChanged;

    /// <summary>Gets the latest connectivity the host reported (<c>connectivityChanged</c>), or <see langword="null"/> before the first report.</summary>
    ConnectivityState? LatestConnectivity { get; }

    /// <summary>Raised on a background thread when the host sends <c>connectivityChanged</c>.</summary>
    event EventHandler<ConnectivityState>? ConnectivityChanged;

    /// <summary>
    /// Gets a task that completes when the connection is lost or the process exits. Its result describes what
    /// happened (for example "host process exited with code 3").
    /// </summary>
    Task<string> Completion { get; }

    /// <summary>Gets a proxy for a host service on this connection.</summary>
    /// <typeparam name="T">The service interface.</typeparam>
    /// <returns>The proxy.</returns>
    T GetProxy<T>()
        where T : class;

    /// <summary>
    /// Asks the host to shut down, waits up to <paramref name="timeout"/> for it to exit and kills the process tree
    /// if it is still alive.
    /// </summary>
    /// <param name="timeout">How long to wait for a clean exit.</param>
    /// <returns>A task that completes when the process is gone.</returns>
    Task ShutdownAsync(TimeSpan timeout);
}

/// <summary>A failed attempt to launch or connect to Pillion Host.</summary>
public sealed class HostLaunchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HostLaunchException"/> class.</summary>
    public HostLaunchException()
        : this("Pillion Host could not be launched.", isRetryable: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostLaunchException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public HostLaunchException(string message)
        : this(message, isRetryable: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostLaunchException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying exception.</param>
    public HostLaunchException(string message, Exception? innerException)
        : this(message, isRetryable: true, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostLaunchException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="isRetryable">Whether backing off and relaunching might help.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public HostLaunchException(string message, bool isRetryable, Exception? innerException = null)
        : base(message, innerException)
    {
        IsRetryable = isRetryable;
    }

    /// <summary>
    /// Gets a value indicating whether the failure is transient. Non-retryable failures (host missing, signature
    /// refused, handshake rejected or version mismatch) put the supervisor straight into Failed.
    /// </summary>
    public bool IsRetryable { get; }
}
