using System.Diagnostics;

namespace Pillion.CommandRunner.Shells;

/// <summary>
/// Finds a Visual Studio installation when the handshake did not name one (a contract 1.3 client, or a host started
/// outside Visual Studio): the newest installation <c>vswhere.exe</c> reports, so the Developer PowerShell environment
/// still loads.
/// </summary>
public static class VisualStudioLocator
{
    /// <summary>Gets the path of <c>vswhere.exe</c> as the Visual Studio Installer places it, or <see langword="null"/> when it is absent.</summary>
    public static string? VsWherePath
    {
        get
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(programFilesX86))
            {
                programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }

            var path = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Finds the newest Visual Studio installation (any product, prereleases included) that has a developer command script.</summary>
    /// <param name="timeout">How long <c>vswhere.exe</c> may take; <see langword="null"/> for 15 s.</param>
    /// <returns>The installation path, or <see langword="null"/> when none was found.</returns>
    public static string? FindNewestInstallation(TimeSpan? timeout = null)
    {
        var vswhere = VsWherePath;
        if (vswhere is null)
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(vswhere)
                {
                    Arguments = "-all -prerelease -products * -sort -property installationPath -nologo",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds))
            {
                process.Kill();
                return null;
            }

            // -sort puts the newest version first; the first one with VsDevCmd.bat wins.
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (DeveloperEnvironment.FindScript(line) is not null)
                {
                    return line;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
