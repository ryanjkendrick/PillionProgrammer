using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Pillion.Host.Rpc;

/// <summary>
/// The per-launch secret the VSIX hands the host through an inherited anonymous pipe: 32 random bytes encoded as
/// base64url without padding (43 characters). The value is never exposed: <see cref="ToString"/> is redacted and
/// <see cref="Matches"/> compares in constant time.
/// </summary>
internal sealed class LaunchToken
{
    /// <summary>The length of a token in characters.</summary>
    public const int Length = 43;

    private readonly byte[] _value;

    private LaunchToken(byte[] value)
    {
        _value = value;
    }

    /// <summary>Validates and wraps a token.</summary>
    /// <param name="value">The token text.</param>
    /// <returns>The token.</returns>
    /// <exception cref="FormatException">The value is not 43 base64url characters. The message never contains the value.</exception>
    public static LaunchToken Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != Length)
        {
            throw new FormatException(
                $"The launch token must be {Length} base64url characters; {value.Length} characters were received.");
        }

        foreach (var c in value)
        {
            if (!IsBase64UrlChar(c))
            {
                throw new FormatException("The launch token contains a character that is not base64url.");
            }
        }

        return new LaunchToken(Encoding.ASCII.GetBytes(value));
    }

    /// <summary>Creates a new random token (for tests and tools; the VSIX creates the real one).</summary>
    /// <returns>The token text.</returns>
    public static string CreateValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Compares a candidate with this token in constant time.</summary>
    /// <param name="candidate">The token a client presented.</param>
    /// <returns><see langword="true"/> when the candidate equals this token.</returns>
    public bool Matches(string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(candidate);
        try
        {
            return CryptographicOperations.FixedTimeEquals(_value, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => "[launch token]";

    private static bool IsBase64UrlChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
}

/// <summary>Supplies the launch token. Production reads it from the inherited pipe; tests inject one.</summary>
internal interface ILaunchTokenSource
{
    /// <summary>Gets the launch token.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The token.</returns>
    Task<LaunchToken> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the launch token from the inherited anonymous pipe named by <c>--token-handle</c>: exactly one UTF-8 line
/// (an optional trailing carriage return is ignored), after which the pipe is closed.
/// </summary>
internal sealed class AnonymousPipeLaunchTokenSource : ILaunchTokenSource
{
    /// <summary>How long to wait for the token before giving up.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // Generous upper bound for one line; anything longer is rejected without being kept.
    private const int MaxLineBytes = 256;

    private readonly string _handle;

    /// <summary>Initializes a new instance of the <see cref="AnonymousPipeLaunchTokenSource"/> class.</summary>
    /// <param name="handle">The decimal handle of the pipe's read end, as passed on the command line.</param>
    public AnonymousPipeLaunchTokenSource(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        _handle = handle;
    }

    /// <inheritdoc/>
    public async Task<LaunchToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        // Anonymous pipes only support synchronous reads, so read on a pool thread and bound the wait.
        var line = await Task.Run(ReadLine, CancellationToken.None)
            .WaitAsync(ReadTimeout, cancellationToken)
            .ConfigureAwait(false);
        return LaunchToken.Parse(line);
    }

    private string ReadLine()
    {
        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, _handle);
        var buffer = new byte[MaxLineBytes];
        try
        {
            var count = 0;
            while (true)
            {
                var b = pipe.ReadByte();
                if (b is -1 or '\n')
                {
                    break;
                }

                if (count == buffer.Length)
                {
                    throw new InvalidDataException("The launch token line is too long.");
                }

                buffer[count++] = (byte)b;
            }

            if (count > 0 && buffer[count - 1] == '\r')
            {
                count--;
            }

            return Encoding.UTF8.GetString(buffer, 0, count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
