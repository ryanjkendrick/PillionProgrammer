using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.CommandRunner.Shells;

/// <summary>The outcome of loading a Visual Studio installation's developer environment.</summary>
/// <param name="Environment">The captured variables, or <see langword="null"/> when the environment could not be loaded.</param>
/// <param name="Warning">Why it could not be loaded, or <see langword="null"/>.</param>
public sealed record DeveloperEnvironmentResult(IReadOnlyDictionary<string, string>? Environment, string? Warning)
{
    /// <summary>Gets a value indicating whether the environment loaded.</summary>
    public bool Loaded => Environment is not null;
}

/// <summary>
/// Captures the Developer PowerShell environment of a Visual Studio installation (design section 8): the variables
/// <c>Common7\Tools\VsDevCmd.bat</c> sets (MSBuild, the compilers and the SDKs on <c>PATH</c>, <c>VSINSTALLDIR</c>,
/// <c>VCToolsInstallDir</c> and the rest). <c>Launch-VsDevShell.ps1</c> wraps the same batch file, so the batch file is
/// run directly under <c>cmd.exe</c> and its <c>set</c> output parsed; no PowerShell start-up is paid.
/// </summary>
public static partial class DeveloperEnvironment
{
    /// <summary>The default time the batch file may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Finds the developer command script of an installation.</summary>
    /// <param name="installationPath">The Visual Studio installation path (<c>C:\Program Files\Microsoft Visual Studio\18\Community</c>).</param>
    /// <returns>The path of <c>VsDevCmd.bat</c>, or <see langword="null"/> when the installation has none.</returns>
    public static string? FindScript(string? installationPath)
    {
        if (string.IsNullOrWhiteSpace(installationPath))
        {
            return null;
        }

        try
        {
            var script = Path.Combine(installationPath, "Common7", "Tools", "VsDevCmd.bat");
            return File.Exists(script) ? script : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Runs the installation's <c>VsDevCmd.bat</c> for this machine's architecture and captures the environment it leaves.</summary>
    /// <param name="installationPath">The Visual Studio installation path.</param>
    /// <param name="timeout">How long the script may take; <see langword="null"/> for <see cref="DefaultTimeout"/>.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>The environment, or a warning that says why the plain environment will be used instead.</returns>
    public static async Task<DeveloperEnvironmentResult> CaptureAsync(string? installationPath, TimeSpan? timeout = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(installationPath))
        {
            return new DeveloperEnvironmentResult(null, "No Visual Studio installation path was given; commands run with the plain environment, without the Developer PowerShell variables.");
        }

        var script = FindScript(installationPath);
        if (script is null)
        {
            return new DeveloperEnvironmentResult(null, $"No Common7\\Tools\\VsDevCmd.bat under '{installationPath}'; commands run with the plain environment, without the Developer PowerShell variables.");
        }

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var start = new ProcessStartInfo(cmd)
        {
            // /d: no AutoRun; /s: the outer quotes are stripped as one unit. The script's own output is discarded by the
            // marker: everything after the marker line is the environment.
            Arguments = $"/d /s /c \"\"{script}\" -no_logo -arch={architecture} -host_arch={architecture} && echo __PILLION_ENV__ && set\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        // The batch file reads VSCMD_* and would otherwise inherit a developer environment the host itself may run in.
        start.Environment["VSCMD_SKIP_SENDTELEMETRY"] = "1";

        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new DeveloperEnvironmentResult(null, $"VsDevCmd.bat could not be started ({exception.Message}); commands run with the plain environment.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        string output;
        string error;
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new DeveloperEnvironmentResult(null, $"VsDevCmd.bat did not finish within {(timeout ?? DefaultTimeout).TotalSeconds:0} s; commands run with the plain environment.");
        }

        if (process.ExitCode != 0)
        {
            var detail = FirstLine(error) ?? FirstLine(output) ?? "no output";
            logger.LogWarning("VsDevCmd.bat exited with {ExitCode} after {Elapsed} ms: {Detail}", process.ExitCode, watch.ElapsedMilliseconds, detail);
            return new DeveloperEnvironmentResult(null, $"VsDevCmd.bat exited with {process.ExitCode} ({detail}); commands run with the plain environment.");
        }

        var marker = output.IndexOf("__PILLION_ENV__", StringComparison.Ordinal);
        var environment = Parse(marker < 0 ? output : output[(marker + "__PILLION_ENV__".Length)..]);
        if (!environment.ContainsKey("VSINSTALLDIR"))
        {
            return new DeveloperEnvironmentResult(null, "VsDevCmd.bat ran but set no VSINSTALLDIR; commands run with the plain environment.");
        }

        logger.LogInformation("Loaded the Developer PowerShell environment of {Path} ({Count} variables) in {Elapsed} ms", installationPath, environment.Count, watch.ElapsedMilliseconds);
        return new DeveloperEnvironmentResult(environment, null);
    }

    /// <summary>Parses the output of <c>set</c>.</summary>
    /// <param name="setOutput">Lines of <c>NAME=value</c>.</param>
    /// <returns>The variables, case-insensitive by name.</returns>
    internal static Dictionary<string, string> Parse(string setOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in setOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            result[line[..separator]] = line[(separator + 1)..];
        }

        return result;
    }

    private static string? FirstLine(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// Loads each Visual Studio installation's developer environment once per host and hands every session the same
/// dictionary: the batch file takes seconds, and every agent session of a Visual Studio instance names the same path.
/// Thread-safe; register it as a singleton.
/// </summary>
public sealed class DeveloperEnvironmentCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DeveloperEnvironmentResult>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="DeveloperEnvironmentCache"/> class.</summary>
    /// <param name="loggerFactory">Where to log; may be <see langword="null"/>.</param>
    public DeveloperEnvironmentCache(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger(typeof(DeveloperEnvironment));
    }

    /// <summary>Gets the environment of an installation, loading it on first use. A failed load is not cached, so a later session retries.</summary>
    /// <param name="installationPath">The Visual Studio installation path.</param>
    /// <param name="cancellationToken">Cancels this caller's wait; the load itself continues for the others.</param>
    /// <returns>The result.</returns>
    public async Task<DeveloperEnvironmentResult> GetAsync(string? installationPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installationPath))
        {
            return await DeveloperEnvironment.CaptureAsync(installationPath, cancellationToken, logger: _logger).ConfigureAwait(false);
        }

        var key = Path.TrimEndingDirectorySeparator(installationPath);
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<DeveloperEnvironmentResult>>(() => DeveloperEnvironment.CaptureAsync(k, CancellationToken.None, logger: _logger)));
        var result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Loaded)
        {
            _cache.TryRemove(key, out _);
        }

        return result;
    }
}
