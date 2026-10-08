using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The launch protocol shared with Pillion Host (design doc section 4, "Process boundary contract"): token format,
/// pipe name format and the host command line.
/// </summary>
public static class LaunchProtocol
{
    /// <summary>The number of random bytes in a protocol token.</summary>
    public const int TokenByteLength = 32;

    /// <summary>The length of a protocol token: 32 bytes as base64url without padding.</summary>
    public const int TokenLength = 43;

    /// <summary>The prefix of every pipe name.</summary>
    public const string PipeNamePrefix = "pillion-";

    /// <summary>The file name of the host executable inside the extension's <c>Host</c> folder.</summary>
    public const string HostExecutableName = "Pillion.Host.exe";

    /// <summary>The folder, relative to the extension install directory, that holds the published host.</summary>
    public const string HostFolderName = "Host";

    /// <summary>Generates a new per-launch protocol token: 32 bytes from a CSPRNG, base64url without padding.</summary>
    /// <returns>The 43-character token.</returns>
    public static string GenerateToken()
    {
        var bytes = new byte[TokenByteLength];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        return Base64UrlEncode(bytes);
    }

    /// <summary>Encodes bytes as base64url (RFC 4648 section 5) without padding.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The encoded text.</returns>
    public static string Base64UrlEncode(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Determines whether text has the shape of a protocol token.</summary>
    /// <param name="token">The text.</param>
    /// <returns><see langword="true"/> for 43 base64url characters.</returns>
    public static bool IsWellFormedToken(string? token)
    {
        if (token is null || token.Length != TokenLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!IsBase64UrlChar(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates a pipe name, <c>pillion-&lt;devenv pid&gt;-&lt;8 random hex&gt;</c>.</summary>
    /// <param name="processId">The devenv.exe process id.</param>
    /// <returns>The pipe name.</returns>
    public static string CreatePipeName(int processId)
    {
        var bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var hex = new StringBuilder(8);
        foreach (var b in bytes)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return PipeNamePrefix + processId.ToString(CultureInfo.InvariantCulture) + "-" + hex;
    }

    /// <summary>Builds the host's argument list in protocol order.</summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="parentProcessId">The devenv.exe process id.</param>
    /// <param name="tokenHandle">The inheritable read handle of the anonymous token pipe, as a string.</param>
    /// <param name="logDirectory">The log directory, or <see langword="null"/> to omit <c>--log-dir</c>.</param>
    /// <returns>The arguments, unquoted.</returns>
    public static IReadOnlyList<string> BuildArguments(string pipeName, int parentProcessId, string tokenHandle, string? logDirectory)
    {
        var args = new List<string>
        {
            "--pipe", pipeName,
            "--parent-pid", parentProcessId.ToString(CultureInfo.InvariantCulture),
            "--token-handle", tokenHandle,
        };

        if (!string.IsNullOrEmpty(logDirectory))
        {
            args.Add("--log-dir");
            args.Add(logDirectory!.TrimEnd('\\'));
        }

        return args;
    }

    /// <summary>
    /// Joins arguments into a Windows command line that <c>CommandLineToArgvW</c> and the .NET runtime split back into
    /// the same strings.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line.</returns>
    public static string JoinArguments(IEnumerable<string> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            AppendQuoted(builder, argument ?? string.Empty);
        }

        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }

    private static bool IsBase64UrlChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
}
