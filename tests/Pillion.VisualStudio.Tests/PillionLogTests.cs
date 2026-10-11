using System;
using System.Collections.Generic;
using System.IO;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public sealed class PillionLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 2, 3, 123, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pillion-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Format_IsOneStructuredLine()
    {
        var line = PillionLog.Format(Now, PillionLogLevel.Info, "host.launched", "Started Pillion Host.", [("pid", 1234), ("pipe", "pillion-1-0a1b2c3d")]);

        Assert.Equal("2026-10-08T14:02:03.123Z info host.launched pid=1234 pipe=pillion-1-0a1b2c3d message=\"Started Pillion Host.\"", line);
    }

    [Fact]
    public void Format_QuotesAndEscapesValues()
    {
        var line = PillionLog.Format(Now, PillionLogLevel.Warning, "e", "two\nlines", [("path", @"C:\a b\c"), ("empty", null), ("eq", "a=b")]);

        Assert.Equal(@"2026-10-08T14:02:03.123Z warn e path=""C:\\a b\\c"" empty="""" eq=""a=b"" message=""two\nlines""", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void FileName_IsVsixAndTheUtcDate() =>
        Assert.Equal("vsix-20261008.log", PillionLog.FileNameFor(new DateTimeOffset(2026, 10, 9, 0, 30, 0, TimeSpan.FromHours(2))));

    [Fact]
    public void Write_AppendsToTheDailyFile()
    {
        var log = new PillionLog(_directory, () => Now);

        log.Info("a", "first");
        log.Debug("b", "second");

        var lines = File.ReadAllLines(Path.Combine(_directory, "vsix-20261008.log"));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("message=first", lines[0]);
        Assert.Contains(" debug b ", lines[1]);
        Assert.Equal(Path.Combine(_directory, "vsix-20261008.log"), log.CurrentFilePath);
    }

    [Fact]
    public void Write_InBackground_QueuesLinesInOrder_AndFlushWaitsForTheFile()
    {
        var log = new PillionLog(_directory, () => Now, writeInBackground: true);

        for (var i = 0; i < 200; i++)
        {
            log.Info("e", "line " + i);
        }

        Assert.True(log.Flush(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(Path.Combine(_directory, "vsix-20261008.log"));
        Assert.Equal(200, lines.Length);
        for (var i = 0; i < 200; i++)
        {
            Assert.EndsWith("message=\"line " + i + "\"", lines[i]);
        }
    }

    [Fact]
    public void Write_InBackground_DoesNotWaitForTheFile()
    {
        // The file is held open without sharing: a synchronous log would hit the IOException path and lose the line, and
        // the caller must not be the one to wait. Once the file is released the queued line is written.
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "vsix-20261008.log");
        var log = new PillionLog(_directory, () => Now, writeInBackground: true);

        using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            log.Info("e", "while locked");
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), "Logging waited for the file: " + clock.Elapsed);
        }

        log.Info("e", "after");
        Assert.True(log.Flush(TimeSpan.FromSeconds(10)));
        Assert.Contains("after", File.ReadAllText(path));
    }

    [Fact]
    public void Flush_WithNothingQueued_ReturnsAtOnce() =>
        Assert.True(new PillionLog(_directory, () => Now, writeInBackground: true).Flush(TimeSpan.FromSeconds(1)));

    [Fact]
    public void Sink_ReceivesInfoAndAbove()
    {
        var sunk = new List<string>();
        var log = new PillionLog(directory: null, () => Now) { Sink = sunk.Add };

        log.Debug("d", "hidden");
        log.Info("i", "shown");
        log.Warning("w", "shown");
        log.Error("e", "shown");

        Assert.Equal(3, sunk.Count);
    }

    [Fact]
    public void Secrets_AreRedactedEverywhere()
    {
        var sunk = new List<string>();
        var log = new PillionLog(_directory, () => Now) { Sink = sunk.Add };
        var token = LaunchProtocol.GenerateToken();
        log.AddSecret(token);

        log.Info("leak", "token is " + token, ("value", token));

        var file = File.ReadAllText(Path.Combine(_directory, "vsix-20261008.log"));
        Assert.DoesNotContain(token, file);
        Assert.Contains("[redacted]", file);
        Assert.DoesNotContain(token, sunk[0]);

        log.RemoveSecret(token);
        log.Info("after", token);
        Assert.Contains(token, sunk[1]);
    }

    [Fact]
    public void Write_NeverThrows()
    {
        var log = new PillionLog(_directory, () => Now) { Sink = _ => throw new InvalidOperationException() };

        log.Info("x", "y");
    }
}
