using Microsoft.Extensions.Logging;
using Pillion.CommandRunner;
using Pillion.CommandRunner.Shells;
using Pillion.Host.Rpc;

namespace Pillion.Host.CommandSessions;

/// <summary>
/// Creates Command Runner sessions for the host (work item 3.11, behind <c>features.commandRunner</c>) with the
/// launching Visual Studio's installation path from the handshake (contract 1.4), so every session loads that
/// installation's Developer PowerShell environment. A 1.3 client sends no path; then the newest installation on the
/// machine is used. The agent tools (work item 3.12) and the Terminal window (3.15) create their sessions through this.
/// </summary>
internal sealed partial class HostCommandSessions
{
    private readonly ICommandRunner _runner;
    private readonly IVsixConnection? _connection;
    private readonly ILogger<HostCommandSessions> _logger;
    private readonly Lazy<string?> _fallbackInstallation;

    /// <summary>Initializes a new instance of the <see cref="HostCommandSessions"/> class.</summary>
    /// <param name="runner">The Command Runner.</param>
    /// <param name="connection">The VSIX connection, whose handshake names the installation; may be <see langword="null"/>.</param>
    /// <param name="loggerFactory">Where to log; may be <see langword="null"/>.</param>
    public HostCommandSessions(ICommandRunner runner, IVsixConnection? connection, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _connection = connection;
        _logger = loggerFactory?.CreateLogger<HostCommandSessions>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HostCommandSessions>.Instance;
        _fallbackInstallation = new Lazy<string?>(() => VisualStudioLocator.FindNewestInstallation());
    }

    /// <summary>Gets the Visual Studio installation path sessions load their developer environment from: the handshake's, or the newest installation found, or <see langword="null"/>.</summary>
    public string? VisualStudioInstallationPath
    {
        get
        {
            var fromHandshake = _connection?.Client?.VisualStudioInstallationPath;
            if (!string.IsNullOrWhiteSpace(fromHandshake))
            {
                return fromHandshake;
            }

            var fallback = _fallbackInstallation.Value;
            if (fallback is not null)
            {
                LogFallbackInstallation(_logger, fallback);
            }

            return fallback;
        }
    }

    /// <summary>Creates a session starting in a directory (the solution root), with the installation path filled in.</summary>
    /// <param name="workingDirectory">The directory commands start in.</param>
    /// <param name="configure">Further options; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the set-up.</param>
    /// <returns>The session.</returns>
    /// <exception cref="Contracts.PillionRpcException"><c>features.commandRunner</c> is off.</exception>
    public Task<ICommandSession> CreateAsync(string workingDirectory, Action<CommandSessionOptions>? configure = null, CancellationToken cancellationToken = default)
    {
        var options = new CommandSessionOptions(workingDirectory) { VisualStudioInstallationPath = VisualStudioInstallationPath };
        configure?.Invoke(options);
        return _runner.CreateSessionAsync(options, cancellationToken);
    }

    [LoggerMessage(EventId = 3140, Level = LogLevel.Information, Message = "The handshake named no Visual Studio installation; the Command Runner uses the newest one found, {Path}.")]
    private static partial void LogFallbackInstallation(ILogger logger, string path);
}
