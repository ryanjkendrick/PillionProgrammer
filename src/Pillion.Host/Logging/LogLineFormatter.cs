using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pillion.Host.Logging;

/// <summary>
/// Formats one log event as a single JSON line:
/// <c>{"ts":…,"level":…,"category":…,"eventId":…,"eventName":…,"message":…,"exception":…,"state":{…}}</c>.
/// <c>eventName</c>, <c>exception</c> and <c>state</c> are omitted when empty.
/// </summary>
internal static class LogLineFormatter
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>Formats a log event.</summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="timestamp">When the event happened.</param>
    /// <param name="level">The level.</param>
    /// <param name="category">The logger category.</param>
    /// <param name="eventId">The event id.</param>
    /// <param name="message">The formatted message.</param>
    /// <param name="exception">The exception, if any (written with its stack trace).</param>
    /// <param name="state">The structured state; key-value pairs other than the template are written under <c>state</c>.</param>
    /// <returns>The JSON line, without a trailing newline.</returns>
    public static string Format<TState>(
        DateTimeOffset timestamp,
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        TState state)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            writer.WriteString("level", level.ToString());
            writer.WriteString("category", category);
            writer.WriteNumber("eventId", eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name))
            {
                writer.WriteString("eventName", eventId.Name);
            }

            writer.WriteString("message", message);
            if (exception is not null)
            {
                writer.WriteString("exception", exception.ToString());
            }

            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs && pairs.Count > 1)
            {
                writer.WriteStartObject("state");
                foreach (var pair in pairs)
                {
                    if (pair.Key == OriginalFormatKey)
                    {
                        continue;
                    }

                    writer.WriteString(pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
