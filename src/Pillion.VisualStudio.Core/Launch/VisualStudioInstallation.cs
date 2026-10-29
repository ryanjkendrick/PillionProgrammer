using System;
using System.Diagnostics;
using System.IO;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The Visual Studio installation the extension runs in, reported to the host in the handshake (contract 1.4,
/// <see cref="Contracts.HandshakeRequest.VisualStudioInstallationPath"/>) so the Command Runner can load its Developer
/// PowerShell environment (work item 3.11).
/// </summary>
public sealed class VisualStudioInstallation
{
    /// <summary>An installation that is not known (a process other than devenv.exe, such as a test host).</summary>
    public static readonly VisualStudioInstallation Unknown = new(string.Empty, string.Empty);

    /// <summary>Initializes a new instance of the <see cref="VisualStudioInstallation"/> class.</summary>
    /// <param name="installationPath">The folder holding <c>Common7\IDE</c>, or empty.</param>
    /// <param name="version">The product version, or empty.</param>
    public VisualStudioInstallation(string installationPath, string version)
    {
        InstallationPath = installationPath ?? string.Empty;
        Version = version ?? string.Empty;
    }

    /// <summary>Gets the installation path (the folder holding <c>Common7\IDE</c>), or empty when unknown.</summary>
    public string InstallationPath { get; }

    /// <summary>Gets the product version (for example <c>17.14.36221.1</c>), or empty when unknown.</summary>
    public string Version { get; }

    /// <summary>
    /// Detects the installation from the current process: when it is <c>devenv.exe</c> under <c>Common7\IDE</c>, the folder two
    /// levels up and the executable's product version; otherwise <see cref="Unknown"/>. Reads no registry and asks no Visual
    /// Studio service, so it is safe off the UI thread.
    /// </summary>
    /// <returns>The installation.</returns>
    public static VisualStudioInstallation FromCurrentProcess()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var exe = current.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), "devenv.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Unknown;
            }

            var ide = Path.GetDirectoryName(exe);            // ...\Common7\IDE
            var common7 = ide is null ? null : Path.GetDirectoryName(ide);
            var root = common7 is null ? null : Path.GetDirectoryName(common7);
            if (root is null || !string.Equals(Path.GetFileName(ide), "IDE", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(common7), "Common7", StringComparison.OrdinalIgnoreCase))
            {
                return Unknown;
            }

            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty;
            return new VisualStudioInstallation(root, version);
        }
#pragma warning disable CA1031 // Not knowing the installation only costs the Developer PowerShell environment; it must not break the launch.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unknown;
        }
    }
}
