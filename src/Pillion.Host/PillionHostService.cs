using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pillion.Contracts;

namespace Pillion.Host;

/// <summary>Options for the running host.</summary>
/// <param name="PipeName">The named pipe the host serves RPC on.</param>
internal sealed record PillionHostOptions(string PipeName);

/// <summary>
/// The host's main background service. For now it only logs start-up and shutdown; the RPC server on
/// <see cref="PillionHostOptions.PipeName"/> is added in a later work item.
/// </summary>
internal sealed partial class PillionHostService : BackgroundService
{
    private readonly PillionHostOptions _options;
    private readonly ILogger<PillionHostService> _logger;

    /// <summary>Initializes a new instance of the <see cref="PillionHostService"/> class.</summary>
    /// <param name="options">The host options.</param>
    /// <param name="logger">The logger.</param>
    public PillionHostService(PillionHostOptions options, ILogger<PillionHostService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, HostInfo.Version, ContractVersion.Text, Environment.ProcessId, _options.PipeName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown (Ctrl+C or host stop).
        }

        LogStopping(_logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pillion Host {Version} started (contract {Contract}, pid {ProcessId}, pipe '{PipeName}').")]
    private static partial void LogStarted(ILogger logger, string version, string contract, int processId, string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pillion Host stopping.")]
    private static partial void LogStopping(ILogger logger);
}
