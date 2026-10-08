using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Pillion.Host.Logging;

/// <summary>Settings for <see cref="RollingFileLoggerProvider"/>.</summary>
internal sealed class RollingFileLoggerOptions
{
    /// <summary>Gets the default log folder: <c>%LOCALAPPDATA%\PillionProgrammer\logs</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillionProgrammer", "logs");

    /// <summary>Gets or sets the log folder.</summary>
    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>Gets or sets the file name prefix; files are named <c>{prefix}YYYYMMDD.log</c>.</summary>
    public string FilePrefix { get; set; } = "host-";

    /// <summary>Gets or sets the maximum age of a log file, applied at start-up.</summary>
    public TimeSpan MaxAge { get; set; } = LogRetention.DefaultMaxAge;

    /// <summary>Gets or sets the cap on the total size of the log files, applied at start-up.</summary>
    public long MaxTotalBytes { get; set; } = LogRetention.DefaultMaxTotalBytes;
}

/// <summary>
/// A small rolling file sink: one JSON line per event (see <see cref="LogLineFormatter"/>) in
/// <c>{prefix}YYYYMMDD.log</c> (local date), a new file each day, retention applied at start-up. If another process
/// already has today's file open, this process writes <c>{prefix}YYYYMMDD-{pid}.log</c> instead.
/// </summary>
[ProviderAlias("PillionFile")]
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileWriter _writer;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="RollingFileLoggerProvider"/> class and applies retention.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="time">The clock (local time decides the file name).</param>
    public RollingFileLoggerProvider(RollingFileLoggerOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;

        try
        {
            System.IO.Directory.CreateDirectory(options.Directory);
            LogRetention.Apply(options.Directory, options.FilePrefix, options.MaxAge, options.MaxTotalBytes, time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Pillion Host: cannot prepare the log folder '{options.Directory}': {ex.Message}");
        }

        _writer = new RollingFileWriter(options.Directory, options.FilePrefix);
    }

    /// <summary>Gets the path of the file currently being written, if one is open.</summary>
    public string? CurrentPath => _writer.CurrentPath;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, _writer, _time);

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();

    private sealed class RollingFileLogger(string category, RollingFileWriter writer, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var now = time.GetLocalNow();
            var line = LogLineFormatter.Format(now, logLevel, category, eventId, formatter(state, exception), exception, state);
            writer.WriteLine(now, line);
        }
    }
}

/// <summary>Appends lines to the day's log file. Thread-safe; I/O errors drop the line rather than throw.</summary>
internal sealed class RollingFileWriter : IDisposable
{
    private readonly Lock _lock = new();
    private readonly string _directory;
    private readonly string _prefix;
    private StreamWriter? _stream;
    private DateOnly _date;
    private DateOnly? _failedDate;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RollingFileWriter"/> class.</summary>
    /// <param name="directory">The log folder.</param>
    /// <param name="prefix">The file name prefix.</param>
    public RollingFileWriter(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
    }

    /// <summary>Gets the path of the file currently open, if any.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Appends a line to the file for <paramref name="timestamp"/>'s date, rolling to a new file if needed.</summary>
    /// <param name="timestamp">The event's local time.</param>
    /// <param name="line">The line, without a newline.</param>
    public void WriteLine(DateTimeOffset timestamp, string line)
    {
        var date = DateOnly.FromDateTime(timestamp.DateTime);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_stream is null || date != _date)
            {
                if (_failedDate == date)
                {
                    return;
                }

                Open(date);
                if (_stream is null)
                {
                    return;
                }
            }

            try
            {
                _stream.Write(line);
                _stream.Write('\n');
                _stream.Flush();
            }
            catch (IOException)
            {
                // Disk full or similar: drop the line; logging must never take the host down.
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Open(DateOnly date)
    {
        _stream?.Dispose();
        _stream = null;
        CurrentPath = null;
        _date = date;

        var stem = _prefix + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string[] candidates = [stem + ".log", $"{stem}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.log"];
        foreach (var name in candidates)
        {
            var path = Path.Combine(_directory, name);
            try
            {
                var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                _stream = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                CurrentPath = path;
                _failedDate = null;
                return;
            }
            catch (IOException)
            {
                // Probably open in another host process; try the per-process name.
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                break;
            }
        }

        _failedDate = date;
    }
}
