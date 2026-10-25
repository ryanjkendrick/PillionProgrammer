using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pillion.ModelGateway;

/// <summary>
/// Tool results as they go back to a model: text, capped at 16 KB of UTF-8 (design section 8) with a truncation marker.
/// The cap is applied in the gateway before transport, so the size the Outbound log records is the size that was sent.
/// </summary>
public static class ToolResults
{
    /// <summary>The most bytes of UTF-8 a tool result may carry to the model, the marker included.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The marker appended to a truncated result.</summary>
    /// <param name="shown">The bytes kept.</param>
    /// <param name="total">The bytes of the original.</param>
    /// <returns>The marker text.</returns>
    public static string Marker(int shown, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"\n[truncated by Pillion: {shown} of {total} bytes shown; ask for the rest with read_more]");

    /// <summary>Turns a result object into the text sent to the model: a string as it is, anything else as JSON.</summary>
    /// <param name="result">The result of a <see cref="FunctionResultContent"/>.</param>
    /// <returns>The text; empty for <see langword="null"/>.</returns>
    public static string Text(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText(),
        _ => JsonSerializer.Serialize(result),
    };

    /// <summary>Caps a result at <see cref="MaxBytes"/> of UTF-8, never splitting a surrogate pair.</summary>
    /// <param name="text">The result text.</param>
    /// <returns>The text to send, its size in bytes and whether it was truncated.</returns>
    public static CappedToolResult Cap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var total = Encoding.UTF8.GetByteCount(text);
        if (total <= MaxBytes)
        {
            return new CappedToolResult(text, total, Truncated: false, total);
        }

        // Room for the marker, whose digits are at most those of the total byte count twice.
        var markerReserve = Encoding.UTF8.GetByteCount(Marker(total, total));
        var budget = MaxBytes - markerReserve;
        var kept = CharsWithin(text, budget);
        var head = text[..kept];
        var marker = Marker(Encoding.UTF8.GetByteCount(head), total);
        var sent = head + marker;
        return new CappedToolResult(sent, Encoding.UTF8.GetByteCount(sent), Truncated: true, total);
    }

    /// <summary>The number of leading characters of <paramref name="text"/> whose UTF-8 fits in <paramref name="bytes"/>, not ending inside a surrogate pair.</summary>
    private static int CharsWithin(string text, int bytes)
    {
        var used = 0;
        var i = 0;
        while (i < text.Length)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(i, length));
            if (used + size > bytes)
            {
                break;
            }

            used += size;
            i += length;
        }

        return i;
    }
}

/// <summary>A tool result after the cap.</summary>
/// <param name="Text">The text to send.</param>
/// <param name="Bytes">Its size in UTF-8 bytes (what the Outbound log records).</param>
/// <param name="Truncated">Whether the original was cut.</param>
/// <param name="OriginalBytes">The size of the original.</param>
public sealed record CappedToolResult(string Text, int Bytes, bool Truncated, int OriginalBytes);
