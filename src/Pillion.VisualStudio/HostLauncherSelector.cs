using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio;

/// <summary>
/// Launches the Pillion Host the extension ships, or, when the <see cref="OverrideVariable"/> environment variable of
/// devenv.exe names an existing executable at launch time, that one instead. The Visual Studio integration tests use it to
/// put <c>tests/Pillion.FakeHost</c> behind the real chat window for one test and restore the real host afterwards
/// (they set the variable inside devenv.exe and use Tools &gt; Pillion: Retry).
/// </summary>
/// <remarks>
/// The override goes through the same launch protocol and the same Authenticode check as the shipped host: a signed
/// extension launches only an executable signed with its own certificate, so the variable cannot swap in another program
/// on a release build. Every launch with the override is logged as a warning.
/// </remarks>
internal sealed class HostLauncherSelector : IHostLauncher
{
    /// <summary>The environment variable that names a stand-in host executable.</summary>
    public const string OverrideVariable = "PILLION_TEST_HOST_PATH";

    private readonly HostLauncherOptions _options;
    private readonly PillionLog _log;
    private readonly HostProcessLauncher _shipped;
    private (string Path, HostProcessLauncher Launcher)? _override;

    /// <summary>Initializes a new instance of the <see cref="HostLauncherSelector"/> class.</summary>
    /// <param name="options">The settings of the shipped host.</param>
    /// <param name="log">The extension log.</param>
    public HostLauncherSelector(HostLauncherOptions options, PillionLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _shipped = new HostProcessLauncher(options, log);
    }

    /// <inheritdoc/>
    public Task<IHostSession> LaunchAsync(CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return _shipped.LaunchAsync(cancellationToken);
        }

        if (_override is not { } current || !string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            var options = new HostLauncherOptions(path!, _options.ExtensionAssemblyPath, _options.LocalTargets) { LogDirectory = _options.LogDirectory };
            current = (path!, new HostProcessLauncher(options, _log));
            _override = current;
        }

        _log.Warning("host.launch.override", "Launching a stand-in host named by " + OverrideVariable + ".", ("path", path));
        return current.Launcher.LaunchAsync(cancellationToken);
    }
}
