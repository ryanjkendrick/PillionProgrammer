using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pillion.Host.Logging;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

public partial class LoggingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retention_DeletesFilesOlderThanSevenDays()
    {
        using var dir = new TempDirectory();
        var old = Create(dir, "host-20260901.log", 10, Now.AddDays(-8));
        var recent = Create(dir, "host-20261005.log", 10, Now.AddDays(-3));

        var deleted = LogRetention.Apply(dir.Path, "host-", LogRetention.DefaultMaxAge, LogRetention.DefaultMaxTotalBytes, Now);

        Assert.Equal([old], deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Retention_TrimsOldestFirstUntilUnderTheSizeCap()
    {
        using var dir = new TempDirectory();
        var oldest = Create(dir, "host-20261001.log", 400, Now.AddDays(-6));
        var middle = Create(dir, "host-20261003.log", 400, Now.AddDays(-4));
        var newest = Create(dir, "host-20261007.log", 400, Now.AddDays(-1));

        var deleted = LogRetention.Apply(dir.Path, "host-", LogRetention.DefaultMaxAge, maxTotalBytes: 900, Now);

        Assert.Equal([oldest], deleted);
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));

        deleted = LogRetention.Apply(dir.Path, "host-", LogRetention.DefaultMaxAge, maxTotalBytes: 400, Now);
        Assert.Equal([middle], deleted);
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Retention_LeavesOtherFilesAlone()
    {
        using var dir = new TempDirectory();
        var vsix = Create(dir, "vsix-20260901.log", 5000, Now.AddDays(-30));
        var notes = Create(dir, "host-notes.txt", 5000, Now.AddDays(-30));
        var host = Create(dir, "host-20260901-1234.log", 10, Now.AddDays(-30));

        var deleted = LogRetention.Apply(dir.Path, "host-", LogRetention.DefaultMaxAge, maxTotalBytes: 1, Now);

        Assert.Equal([host], deleted);
        Assert.True(File.Exists(vsix));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public void Retention_MissingDirectory_DoesNothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));

        Assert.Empty(LogRetention.Apply(missing, "host-", LogRetention.DefaultMaxAge, LogRetention.DefaultMaxTotalBytes, Now));
    }

    [Fact]
    public void Provider_AppliesRetentionAtStartUp()
    {
        using var dir = new TempDirectory();
        var old = Create(dir, "host-20200101.log", 10, DateTimeOffset.UtcNow.AddDays(-30));

        using (new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = dir.Path }, TimeProvider.System))
        {
            Assert.False(File.Exists(old));
        }
    }

    [Fact]
    public void Provider_WritesOneJsonLinePerEventToTheDaysFile()
    {
        using var dir = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero));
        string path;
        using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = dir.Path }, clock))
        {
            var logger = provider.CreateLogger("Pillion.Tests.Category");
            LogSample(logger, "first");
            LogFailure(logger, new InvalidOperationException("broken"));
            path = provider.CurrentPath!;
        }

        Assert.Equal(Path.Combine(dir.Path, "host-20261008.log"), path);
        var lines = ReadLines(path);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        Assert.Equal("2026-10-08T09:30:00.000+00:00", root.GetProperty("ts").GetString());
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal("Pillion.Tests.Category", root.GetProperty("category").GetString());
        Assert.Equal(900, root.GetProperty("eventId").GetInt32());
        Assert.Equal("LogSample", root.GetProperty("eventName").GetString());
        Assert.Equal("Sample first", root.GetProperty("message").GetString());
        Assert.Equal("first", root.GetProperty("state").GetProperty("Value").GetString());
        Assert.False(root.TryGetProperty("exception", out _));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Error", second.RootElement.GetProperty("level").GetString());
        Assert.Contains("InvalidOperationException: broken", second.RootElement.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_RollsToANewFileWhenTheDateChanges()
    {
        using var dir = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero));
        using var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = dir.Path }, clock);
        var logger = provider.CreateLogger("c");

        LogSample(logger, "before midnight");
        clock.Now = clock.Now.AddMinutes(2);
        LogSample(logger, "after midnight");

        Assert.Equal(Path.Combine(dir.Path, "host-20261009.log"), provider.CurrentPath);
        Assert.Single(ReadLines(Path.Combine(dir.Path, "host-20261008.log")));
        Assert.Single(ReadLines(Path.Combine(dir.Path, "host-20261009.log")));
    }

    [Fact]
    public void Provider_FallsBackToAPerProcessFileWhenTodaysFileIsInUse()
    {
        using var dir = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        using var first = new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = dir.Path }, clock);
        using var second = new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = dir.Path }, clock);

        var firstLogger = first.CreateLogger("a");
        var secondLogger = second.CreateLogger("b");
        LogSample(firstLogger, "one");
        LogSample(secondLogger, "two");

        Assert.Equal(Path.Combine(dir.Path, "host-20261008.log"), first.CurrentPath);
        Assert.Equal(Path.Combine(dir.Path, $"host-20261008-{Environment.ProcessId}.log"), second.CurrentPath);
    }

    // The writer keeps the file open; read it the way an editor would (sharing read and write).
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Create(TempDirectory dir, string name, int bytes, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    [LoggerMessage(EventId = 900, Level = LogLevel.Information, Message = "Sample {Value}")]
    private static partial void LogSample(ILogger logger, string value);

    [LoggerMessage(EventId = 901, Level = LogLevel.Error, Message = "Failure")]
    private static partial void LogFailure(ILogger logger, Exception exception);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
