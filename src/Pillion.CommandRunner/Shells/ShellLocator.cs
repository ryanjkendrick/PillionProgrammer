namespace Pillion.CommandRunner.Shells;

/// <summary>The resolved shell of a session: which one and where its executable is.</summary>
/// <param name="Kind">The shell actually used.</param>
/// <param name="ExecutablePath">The full path of its executable.</param>
/// <param name="Warning">Why it differs from the one asked for, or <see langword="null"/>.</param>
internal sealed record ResolvedShell(ShellKind Kind, string ExecutablePath, string? Warning);

/// <summary>Finds the shell executables and writes the command lines that start them on a script.</summary>
internal static class ShellLocator
{
    /// <summary>Resolves a shell, falling back from PowerShell 7 to Windows PowerShell with a warning when <c>pwsh.exe</c> is not installed.</summary>
    /// <param name="kind">The shell asked for.</param>
    /// <returns>The resolved shell.</returns>
    /// <exception cref="FileNotFoundException">Windows PowerShell or cmd is missing, which no supported Windows is.</exception>
    public static ResolvedShell Resolve(ShellKind kind)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        switch (kind)
        {
            case ShellKind.PowerShell7:
                {
                    var pwsh = FindPowerShell7();
                    if (pwsh is not null)
                    {
                        return new ResolvedShell(ShellKind.PowerShell7, pwsh, null);
                    }

                    var fallback = Resolve(ShellKind.WindowsPowerShell);
                    return fallback with { Warning = "PowerShell 7 (pwsh.exe) is not installed; commands run in Windows PowerShell instead." };
                }

            case ShellKind.WindowsPowerShell:
                {
                    var path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
                    return File.Exists(path)
                        ? new ResolvedShell(ShellKind.WindowsPowerShell, path, null)
                        : throw new FileNotFoundException("Windows PowerShell was not found.", path);
                }

            case ShellKind.Cmd:
                {
                    var path = Path.Combine(system, "cmd.exe");
                    return File.Exists(path)
                        ? new ResolvedShell(ShellKind.Cmd, path, null)
                        : throw new FileNotFoundException("cmd.exe was not found.", path);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shell.");
        }
    }

    /// <summary>Gets the script file extension of a shell.</summary>
    /// <param name="kind">The shell.</param>
    /// <returns><c>.ps1</c> or <c>.cmd</c>.</returns>
    public static string ScriptExtension(ShellKind kind) => kind == ShellKind.Cmd ? ".cmd" : ".ps1";

    /// <summary>
    /// Writes the script that runs a command and exits with its exit code. A script file sidesteps every quoting rule of
    /// the shells' <c>-Command</c> and <c>/c</c> switches, so the command runs exactly as the caller wrote it.
    /// </summary>
    /// <param name="kind">The shell.</param>
    /// <param name="command">The command.</param>
    /// <returns>The script text.</returns>
    public static string Script(ShellKind kind, string command)
    {
        if (kind == ShellKind.Cmd)
        {
            // Batch files run line by line; the command may span lines. The echo is off so only the command's output shows.
            return "@echo off\r\n" + command + "\r\nexit /b %ERRORLEVEL%\r\n";
        }

        // PowerShell exits with 1 for any failure unless told the native exit code; a cmdlet error has none, so $? decides.
        return "$ErrorActionPreference = 'Continue'\r\n"
            + command
            + "\r\n$__pillionOk = $?\r\nif ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE } elseif ($__pillionOk) { exit 0 } else { exit 1 }\r\n";
    }

    /// <summary>Builds the command line that starts a shell on a script.</summary>
    /// <param name="shell">The shell.</param>
    /// <param name="scriptPath">The script written by <see cref="Script"/>.</param>
    /// <returns>The command line for <c>CreateProcess</c>.</returns>
    public static string CommandLine(ResolvedShell shell, string scriptPath) => shell.Kind == ShellKind.Cmd
        ? $"\"{shell.ExecutablePath}\" /d /c \"{scriptPath}\""
        : $"\"{shell.ExecutablePath}\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

    private static string? FindPowerShell7()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // An invalid PATH entry.
            }
        }

        return null;
    }
}
