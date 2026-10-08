using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pillion.Host.Lifecycle;

/// <summary>
/// Requests a graceful stop of the host and records the process exit code. Code 0 is a normal stop (the client asked,
/// the parent exited, Ctrl+C); code 1 is an unrecoverable failure. The highest requested code wins.
/// </summary>
internal sealed partial class HostShutdown
{
    /// <summary>The exit code for a normal stop.</summary>
    public const int Success = 0;

    /// <summary>The exit code after an unrecoverable start-up error.</summary>
    public const int Failure = 1;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HostShutdown> _logger;
    private int _exitCode;

    /// <summary>Initializes a new instance of the <see cref="HostShutdown"/> class.</summary>
    /// <param name="lifetime">The application lifetime used to stop the host.</param>
    /// <param name="logger">The logger.</param>
    public HostShutdown(IHostApplicationLifetime lifetime, ILogger<HostShutdown> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Gets the exit code the process should return.</summary>
    public int ExitCode => Volatile.Read(ref _exitCode);

    /// <summary>Gets a token that is cancelled once a stop has been requested.</summary>
    public CancellationToken StopRequested => _lifetime.ApplicationStopping;

    /// <summary>Requests that the host stop. Returns immediately; the stop runs on the thread pool.</summary>
    /// <param name="exitCode">The exit code to report (<see cref="Success"/> or <see cref="Failure"/>).</param>
    /// <param name="reason">Why the host is stopping (logged).</param>
    public void RequestStop(int exitCode, string reason)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _exitCode);
        }
        while (exitCode > current && Interlocked.CompareExchange(ref _exitCode, exitCode, current) != current);

        LogStopRequested(_logger, reason, ExitCode);

        // StopApplication runs ApplicationStopping callbacks synchronously; keep them off the caller's thread (which
        // may be an RPC dispatch that still has to send its response).
        _ = Task.Run(_lifetime.StopApplication);
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Host stop requested: {Reason} (exit code {ExitCode}).")]
    private static partial void LogStopRequested(ILogger logger, string reason, int exitCode);
}
