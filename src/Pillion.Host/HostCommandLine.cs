namespace Pillion.Host;

/// <summary>What the host process has been asked to do.</summary>
internal enum HostMode
{
    /// <summary>The arguments were invalid; print usage and exit.</summary>
    Invalid,

    /// <summary>Print the version and exit.</summary>
    Version,

    /// <summary>Run the host and serve the named pipe.</summary>
    Run,
}

/// <summary>The parsed command line of Pillion Host.</summary>
/// <param name="Mode">What the process should do.</param>
/// <param name="PipeName">The pipe name passed with <c>--pipe</c>, when <paramref name="Mode"/> is <see cref="HostMode.Run"/>.</param>
/// <param name="Error">Why the arguments are invalid, when <paramref name="Mode"/> is <see cref="HostMode.Invalid"/>.</param>
internal sealed record HostCommandLine(HostMode Mode, string? PipeName, string? Error)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage = """
        Usage:
          Pillion.Host --pipe <name>   Run the host and serve RPC on the named pipe <name>.
          Pillion.Host --version       Print the host and contract versions and exit.
        """;

    /// <summary>Parses the process arguments.</summary>
    /// <param name="args">The arguments, without the executable name.</param>
    /// <returns>The parsed command line.</returns>
    public static HostCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var version = false;
        string? pipeName = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--version":
                    version = true;
                    break;

                case "--pipe":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Invalid("--pipe requires a pipe name.");
                    }

                    if (pipeName is not null)
                    {
                        return Invalid("--pipe may only be given once.");
                    }

                    pipeName = args[++i];
                    if (string.IsNullOrWhiteSpace(pipeName))
                    {
                        return Invalid("--pipe requires a non-empty pipe name.");
                    }

                    break;

                default:
                    return Invalid($"Unknown argument '{arg}'.");
            }
        }

        if (version)
        {
            return new HostCommandLine(HostMode.Version, PipeName: null, Error: null);
        }

        return pipeName is null
            ? Invalid("--pipe <name> is required.")
            : new HostCommandLine(HostMode.Run, pipeName, Error: null);
    }

    private static HostCommandLine Invalid(string error) => new(HostMode.Invalid, PipeName: null, error);
}
