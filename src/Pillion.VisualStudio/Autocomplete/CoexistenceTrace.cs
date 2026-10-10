using System;
using System.Globalization;
using System.IO;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// An opt-in timeline of the ghost-text slot for measurements (work item 1.4): when the environment variable
/// <c>PILLION_COEXISTENCE_TRACE</c> names a file, every Pillion and competitor display, rejection, displacement and
/// push is appended to it with a millisecond timestamp. Proposal ids, caret positions and counts only, never text.
/// Off (and free) when the variable is not set.
/// </summary>
internal static class CoexistenceTrace
{
    /// <summary>The environment variable that names the trace file.</summary>
    public const string Variable = "PILLION_COEXISTENCE_TRACE";

    private static readonly string? FilePath = ReadPath();
    private static readonly object Gate = new();

    /// <summary>Gets a value indicating whether tracing is on.</summary>
    public static bool IsOn => FilePath is not null;

    /// <summary>Appends one event.</summary>
    /// <param name="eventName">The event, for example <c>competitor.shown</c>.</param>
    /// <param name="detail">Ids and positions.</param>
    public static void Write(string eventName, string detail = "")
    {
        if (FilePath is not { } path)
        {
            return;
        }

        var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + eventName + (detail.Length == 0 ? string.Empty : " " + detail);
        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Measurement only.
        }
        catch (UnauthorizedAccessException)
        {
            // Measurement only.
        }
    }

    private static string? ReadPath()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
