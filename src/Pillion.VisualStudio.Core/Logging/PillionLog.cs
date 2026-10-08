using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Pillion.VisualStudio.Core;

/// <summary>Severity of a log event.</summary>
public enum PillionLogLevel
{
    /// <summary>Diagnostic detail, written to the file only.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Info,

    /// <summary>Something unexpected that the extension recovered from.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>
/// The VSIX-side logger: one structured line per event, appended to
/// <c>%LOCALAPPDATA%\PillionProgrammer\logs\vsix-YYYYMMDD.log</c> (UTC date) and, from <see cref="PillionLogLevel.Info"/>
/// up, passed to an optional sink (the Output pane).
/// </summary>
/// <remarks>
/// Line format: <c>2026-10-08T14:02:03.123Z info host.launched pid=1234 pipe=pillion-1-0a1b2c3d message="..."</c>.
/// Values containing spaces, quotes or equals signs are quoted. Registered secrets (the protocol token) are replaced
/// by <c>[redacted]</c> in every line as a last line of defence; callers never pass them in the first place.
/// Logging never throws.
/// </remarks>
public sealed class PillionLog
{
    private const string Redacted = "[redacted]";

    private readonly object _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly string? _directory;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="PillionLog"/> class.</summary>
    /// <param name="directory">The log directory, or <see langword="null"/> to write to the sink only.</param>
    /// <param name="now">The time source; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public PillionLog(string? directory, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets or sets a callback that receives each formatted line at <see cref="PillionLogLevel.Info"/> or above,
    /// on the calling thread. The Output pane sink uses a thread-safe write.
    /// </summary>
    public Action<string>? Sink { get; set; }

    /// <summary>Gets the default log directory, <c>%LOCALAPPDATA%\PillionProgrammer\logs</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillionProgrammer", "logs");

    /// <summary>Gets the file the next line written now would go to, or <see langword="null"/> without a directory.</summary>
    public string? CurrentFilePath => _directory is null ? null : Path.Combine(_directory, FileNameFor(_now()));

    /// <summary>Gets the log file name for a time, <c>vsix-YYYYMMDD.log</c> (UTC date).</summary>
    /// <param name="time">The time.</param>
    /// <returns>The file name.</returns>
    public static string FileNameFor(DateTimeOffset time) =>
        "vsix-" + time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    /// <summary>Registers a value that must never appear in the log.</summary>
    /// <param name="secret">The secret.</param>
    public void AddSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            _secrets.Add(secret);
        }
    }

    /// <summary>Forgets a secret registered with <see cref="AddSecret"/>.</summary>
    /// <param name="secret">The secret.</param>
    public void RemoveSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            _secrets.Remove(secret);
        }
    }

    /// <summary>Logs a <see cref="PillionLogLevel.Debug"/> event.</summary>
    /// <param name="eventName">The dotted event name, for example <c>host.connect.attempt</c>.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    public void Debug(string eventName, string message, params (string Key, object? Value)[] fields) =>
        Write(PillionLogLevel.Debug, eventName, message, fields);

    /// <summary>Logs an <see cref="PillionLogLevel.Info"/> event.</summary>
    /// <param name="eventName">The dotted event name.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    public void Info(string eventName, string message, params (string Key, object? Value)[] fields) =>
        Write(PillionLogLevel.Info, eventName, message, fields);

    /// <summary>Logs a <see cref="PillionLogLevel.Warning"/> event.</summary>
    /// <param name="eventName">The dotted event name.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    public void Warning(string eventName, string message, params (string Key, object? Value)[] fields) =>
        Write(PillionLogLevel.Warning, eventName, message, fields);

    /// <summary>Logs an <see cref="PillionLogLevel.Error"/> event.</summary>
    /// <param name="eventName">The dotted event name.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    public void Error(string eventName, string message, params (string Key, object? Value)[] fields) =>
        Write(PillionLogLevel.Error, eventName, message, fields);

    /// <summary>Formats and writes one event.</summary>
    /// <param name="level">The severity.</param>
    /// <param name="eventName">The dotted event name.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    public void Write(PillionLogLevel level, string eventName, string message, params (string Key, object? Value)[] fields)
    {
        try
        {
            var now = _now();
            string line;
            lock (_gate)
            {
                line = Redact(Format(now, level, eventName, message, fields));
                AppendToFile(now, line);
            }

            if (level >= PillionLogLevel.Info)
            {
                Sink?.Invoke(line);
            }
        }
#pragma warning disable CA1031 // Logging must never take the extension down.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Formats one log line (without redaction).</summary>
    /// <param name="time">The event time.</param>
    /// <param name="level">The severity.</param>
    /// <param name="eventName">The dotted event name.</param>
    /// <param name="message">The message.</param>
    /// <param name="fields">Extra key/value pairs.</param>
    /// <returns>The line, without a trailing newline.</returns>
    internal static string Format(DateTimeOffset time, PillionLogLevel level, string eventName, string message, (string Key, object? Value)[]? fields)
    {
        var builder = new StringBuilder(128);
        builder.Append(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        builder.Append(' ').Append(LevelText(level));
        builder.Append(' ').Append(string.IsNullOrEmpty(eventName) ? "event" : eventName);

        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                builder.Append(' ').Append(key).Append('=').Append(QuoteIfNeeded(ValueText(value)));
            }
        }

        builder.Append(" message=").Append(QuoteIfNeeded(message ?? string.Empty));
        return builder.ToString();
    }

    private static string LevelText(PillionLogLevel level) => level switch
    {
        PillionLogLevel.Debug => "debug",
        PillionLogLevel.Info => "info",
        PillionLogLevel.Warning => "warn",
        _ => "error",
    };

    private static string ValueText(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string QuoteIfNeeded(string value)
    {
        var needsQuotes = value.Length == 0;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '=' || char.IsControl(c))
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private string Redact(string line)
    {
        foreach (var secret in _secrets)
        {
            line = line.Replace(secret, Redacted);
        }

        return line;
    }

    private void AppendToFile(DateTimeOffset now, string line)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(Path.Combine(_directory, FileNameFor(now)), line + Environment.NewLine, Utf8NoBom);
        }
        catch (IOException)
        {
            // Another devenv.exe may hold the file briefly; the Output pane still gets the line.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
