using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pillion.Host.Lifecycle;

/// <summary>
/// Stops the host with exit code 0 when the process named by <c>--parent-pid</c> exits. This is a second line of
/// defence beside the Job Object the VSIX puts the host in: it waits on the process handle and also polls, so the host
/// notices within <see cref="PollInterval"/> even if the wait misbehaves.
/// </summary>
internal sealed partial class ParentProcessWatcher : BackgroundService
{
    /// <summary>How often the parent is polled in addition to waiting on its handle.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly PillionHostOptions _options;
    private readonly HostShutdown _shutdown;
    private readonly ILogger<ParentProcessWatcher> _logger;

    /// <summary>Initializes a new instance of the <see cref="ParentProcessWatcher"/> class.</summary>
    /// <param name="options">The host options (for the parent process id).</param>
    /// <param name="shutdown">Used to stop the host.</param>
    /// <param name="logger">The logger.</param>
    public ParentProcessWatcher(PillionHostOptions options, HostShutdown shutdown, ILogger<ParentProcessWatcher> logger)
    {
        _options = options;
        _shutdown = shutdown;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parentId = _options.ParentProcessId;
        using var parent = TryOpen(parentId);
        if (parent is null)
        {
            LogParentExited(_logger, parentId);
            _shutdown.RequestStop(HostShutdown.Success, "parent process is not running");
            return;
        }

        LogWatching(_logger, parentId);

        try
        {
            await Task.WhenAny(WaitForExitAsync(parent, stoppingToken), PollAsync(parent, stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        LogParentExited(_logger, parentId);
        _shutdown.RequestStop(HostShutdown.Success, "parent process exited");
    }

    private static Process? TryOpen(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // Not running.
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            // A parent that started after this process cannot be our parent: the id has been reused.
            using var self = Process.GetCurrentProcess();
            if (process.StartTime > self.StartTime)
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Not allowed to query it; the poll falls back to checking whether the id still exists.
        }

        return process;
    }

    private static async Task WaitForExitAsync(Process parent, CancellationToken cancellationToken)
    {
        try
        {
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Cannot wait on this process; leave it to the poll.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PollAsync(Process parent, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            if (HasExited(parent))
            {
                return;
            }
        }
    }

    private static bool HasExited(Process parent)
    {
        try
        {
            return parent.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            try
            {
                using var again = Process.GetProcessById(parent.Id);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }

    [LoggerMessage(EventId = 120, Level = LogLevel.Information, Message = "Watching parent process {ParentProcessId}.")]
    private static partial void LogWatching(ILogger logger, int parentProcessId);

    [LoggerMessage(EventId = 121, Level = LogLevel.Information, Message = "Parent process {ParentProcessId} has exited; stopping.")]
    private static partial void LogParentExited(ILogger logger, int parentProcessId);
}
