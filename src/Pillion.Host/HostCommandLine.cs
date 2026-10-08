using System.Globalization;

namespace Pillion.Host;

/// <summary>What the host process has been asked to do.</summary>
internal enum HostMode
{
    /// <summary>The arguments were invalid; print usage and exit with code 2.</summary>
    Invalid,

    /// <summary>Print the version and exit.</summary>
    Version,

    /// <summary>Run the host and serve the named pipe.</summary>
    Run,
}

/// <summary>The parsed command line of Pillion Host.</summary>
/// <param name="Mode">What the process should do.</param>
/// <param name="PipeName">The pipe name passed with <c>--pipe</c>, when <paramref name="Mode"/> is <see cref="HostMode.Run"/>.</param>
/// <param name="ParentProcessId">The process id passed with <c>--parent-pid</c>, when <paramref name="Mode"/> is <see cref="HostMode.Run"/>.</param>
/// <param name="TokenHandle">
/// The inherited anonymous pipe handle passed with <c>--token-handle</c> (decimal), when <paramref name="Mode"/> is
/// <see cref="HostMode.Run"/>. This is a handle value, not the token itself.
/// </param>
/// <param name="LogDirectory">The directory passed with <c>--log-dir</c>, or <see langword="null"/> for the default.</param>
/// <param name="Error">Why the arguments are invalid, when <paramref name="Mode"/> is <see cref="HostMode.Invalid"/>.</param>
internal sealed record HostCommandLine(
    HostMode Mode,
    string? PipeName,
    int ParentProcessId,
    string? TokenHandle,
    string? LogDirectory,
    string? Error)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage = """
        Usage:
          Pillion.Host --pipe <name> --parent-pid <pid> --token-handle <handle> [--log-dir <dir>]
              Run the host and serve RPC on the named pipe <name>. <pid> is the process that launched the host
              (the host exits when it exits); <handle> is an inherited anonymous pipe read handle carrying the
              launch token; <dir> overrides the log folder (default %LOCALAPPDATA%\PillionProgrammer\logs).
          Pillion.Host --version
              Print the host and contract versions and exit.
        """;

    /// <summary>Parses the process arguments.</summary>
    /// <param name="args">The arguments, without the executable name.</param>
    /// <returns>The parsed command line.</returns>
    public static HostCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var version = false;
        string? pipeName = null;
        string? parentPid = null;
        string? tokenHandle = null;
        string? logDirectory = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? error;
            switch (arg)
            {
                case "--version":
                    version = true;
                    continue;

                case "--pipe":
                    error = TakeValue(args, ref i, arg, ref pipeName);
                    break;

                case "--parent-pid":
                    error = TakeValue(args, ref i, arg, ref parentPid);
                    break;

                case "--token-handle":
                    error = TakeValue(args, ref i, arg, ref tokenHandle);
                    break;

                case "--log-dir":
                    error = TakeValue(args, ref i, arg, ref logDirectory);
                    break;

                default:
                    return Invalid($"Unknown argument '{arg}'.");
            }

            if (error is not null)
            {
                return Invalid(error);
            }
        }

        if (version)
        {
            return new HostCommandLine(HostMode.Version, null, 0, null, null, null);
        }

        if (pipeName is null)
        {
            return Invalid("--pipe <name> is required.");
        }

        if (parentPid is null)
        {
            return Invalid("--parent-pid <pid> is required.");
        }

        if (tokenHandle is null)
        {
            return Invalid("--token-handle <handle> is required.");
        }

        if (!int.TryParse(parentPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return Invalid("--parent-pid must be a positive decimal process id.");
        }

        if (!long.TryParse(tokenHandle, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return Invalid("--token-handle must be a decimal handle value.");
        }

        return new HostCommandLine(HostMode.Run, pipeName, pid, tokenHandle, logDirectory, null);
    }

    private static string? TakeValue(IReadOnlyList<string> args, ref int i, string name, ref string? value)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return $"{name} requires a value.";
        }

        if (value is not null)
        {
            return $"{name} may only be given once.";
        }

        value = args[++i];
        return string.IsNullOrWhiteSpace(value) ? $"{name} requires a non-empty value." : null;
    }

    private static HostCommandLine Invalid(string error) => new(HostMode.Invalid, null, 0, null, null, error);
}
