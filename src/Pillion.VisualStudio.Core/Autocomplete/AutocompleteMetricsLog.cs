using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Builds the JSON line written for every five-minute window: counts and durations, language identifiers and the
/// degraded flag. There is no field that can hold a file name, a prompt, code or a repository name, and the language
/// identifier is restricted to a few safe characters by <see cref="AutocompleteMetrics"/>.
/// </summary>
public static class AutocompleteMetricsJson
{
    /// <summary>The schema version of the line.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Builds one line (no trailing line break).</summary>
    /// <param name="windowStart">When the window started (UTC).</param>
    /// <param name="windowEnd">When the window ended (UTC).</param>
    /// <param name="stats">The figures of the window.</param>
    /// <param name="degradation">Why autocomplete is degraded at the end of the window, or <see langword="null"/>.</param>
    /// <param name="host">The host's counters, or <see langword="null"/> when it could not be asked.</param>
    /// <returns>The JSON object on one line.</returns>
    public static string Create(DateTimeOffset windowStart, DateTimeOffset windowEnd, AutocompleteStats stats, DegradationInfo? degradation, AutocompleteHostCounters? host)
    {
        if (stats is null)
        {
            throw new ArgumentNullException(nameof(stats));
        }

        var languages = new JObject();
        foreach (var pair in stats.Languages)
        {
            languages[pair.Key] = new JObject
            {
                ["requests"] = pair.Value.Requests,
                ["shown"] = pair.Value.Shown,
                ["accepted"] = pair.Value.Accepted,
            };
        }

        var line = new JObject
        {
            ["schema"] = SchemaVersion,
            ["windowStart"] = Timestamp(windowStart),
            ["windowEnd"] = Timestamp(windowEnd),
            ["seconds"] = (int)Math.Round((windowEnd - windowStart).TotalSeconds),
            ["attempts"] = stats.Attempts,
            ["requests"] = stats.Requests,
            ["shown"] = stats.Shown,
            ["accepted"] = stats.Accepted,
            ["acceptedWord"] = stats.AcceptedWord,
            ["rejected"] = stats.Rejected,
            ["cancelled"] = stats.CancelledByKeystroke,
            ["suppressed"] = stats.Suppressed,
            ["errors"] = stats.Errors,
            ["empty"] = stats.Empty,
            ["multiLine"] = stats.MultiLine,
            ["degradedRequests"] = stats.DegradedRequests,
            ["showRate"] = Round(stats.ShowRate),
            ["acceptanceRate"] = Round(stats.AcceptanceRate),
            ["firstChunkMs"] = Durations(stats.TimeToFirstChunk),
            ["suggestionMs"] = Durations(stats.TimeToSuggestion),
            ["degraded"] = degradation is not null,
            ["degradation"] = degradation is null
                ? JValue.CreateNull()
                : new JObject
                {
                    ["since"] = Timestamp(degradation.Since),
                    ["p95Ms"] = Math.Round(degradation.P95.TotalMilliseconds),
                    ["samples"] = degradation.Samples,
                },
            ["languages"] = languages,
            ["host"] = host is null ? JValue.CreateNull() : HostObject(host),
        };
        return line.ToString(Formatting.None);
    }

    private static JObject HostObject(AutocompleteHostCounters host) => new()
    {
        ["requests"] = host.Requests,
        ["cacheHits"] = host.CacheHits,
        ["typeThroughHits"] = host.TypeThroughHits,
        ["cacheMisses"] = host.CacheMisses,
        ["hitRate"] = Math.Round(host.HitRate, 3),
        ["modelRequests"] = host.ModelRequests,
        ["failed"] = host.Failed,
        ["cancelled"] = host.Cancelled,
        ["windowRequests"] = host.WindowRequests,
        ["windowCacheHits"] = host.WindowCacheHits,
        ["firstTokenMs"] = new JObject { ["p50"] = Round(host.FirstTokenP50Ms), ["p95"] = Round(host.FirstTokenP95Ms) },
        ["totalMs"] = new JObject { ["p50"] = Round(host.TotalP50Ms), ["p95"] = Round(host.TotalP95Ms) },
        ["promptCounted"] = host.PromptCountedRequests,
        ["promptEstimated"] = host.PromptEstimatedRequests,
        ["promptTokensMax"] = host.PromptTokensMax,
    };

    private static JObject Durations(DurationPercentiles percentiles) => new()
    {
        ["count"] = percentiles.Count,
        ["p50"] = percentiles.P50 is { } p50 ? Math.Round(p50.TotalMilliseconds) : JValue.CreateNull(),
        ["p95"] = percentiles.P95 is { } p95 ? Math.Round(p95.TotalMilliseconds) : JValue.CreateNull(),
    };

    private static JToken Round(double? value) => value is { } number ? Math.Round(number, 3) : JValue.CreateNull();

    private static string Timestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Appends the metrics lines to <c>metrics-YYYYMMDD.jsonl</c> (UTC date) in the log folder, next to the
/// <c>vsix-YYYYMMDD.log</c> that <see cref="PillionLog"/> writes, and applies the same retention as the host's logs:
/// files untouched for seven days are deleted, then the oldest until the metrics files take at most 100 MB. Writing
/// never throws. Thread-safe.
/// </summary>
public sealed class AutocompleteMetricsLogWriter
{
    /// <summary>The longest a metrics file is kept.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The most the metrics files may take in total.</summary>
    public const long MaxTotalBytes = 100L * 1024 * 1024;

    private const string Prefix = "metrics-";
    private const string Extension = ".jsonl";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private string? _retentionDay;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteMetricsLogWriter"/> class.</summary>
    /// <param name="directory">The log folder, normally <see cref="PillionLog.DefaultDirectory"/>.</param>
    /// <param name="now">The time source; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public AutocompleteMetricsLogWriter(string directory, Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("A log folder is needed.", nameof(directory));
        }

        _directory = directory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the file name for a time, <c>metrics-YYYYMMDD.jsonl</c> (UTC date).</summary>
    /// <param name="time">The time.</param>
    /// <returns>The file name.</returns>
    public static string FileNameFor(DateTimeOffset time) =>
        Prefix + time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Extension;

    /// <summary>Appends one line. Applies the retention policy the first time a day's file is written.</summary>
    /// <param name="jsonLine">The line, without a line break.</param>
    /// <returns><see langword="true"/> when the line was written.</returns>
    public bool Append(string jsonLine)
    {
        if (string.IsNullOrEmpty(jsonLine))
        {
            return false;
        }

        try
        {
            var now = _now();
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var day = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_retentionDay != day)
                {
                    _retentionDay = day;
                    ApplyRetention(_directory, now, MaxAge, MaxTotalBytes);
                }

                var bytes = Utf8NoBom.GetBytes(jsonLine + "\n");
                using var stream = new FileStream(Path.Combine(_directory, FileNameFor(now)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the <c>metrics-*.jsonl</c> files last written more than <paramref name="maxAge"/> ago, then the oldest
    /// until the rest take at most <paramref name="maxTotalBytes"/>. Other files are never touched; a file in use is skipped.
    /// </summary>
    /// <param name="directory">The log folder.</param>
    /// <param name="now">The current time.</param>
    /// <param name="maxAge">The longest a file is kept.</param>
    /// <param name="maxTotalBytes">The most the files may take in total.</param>
    /// <returns>The paths deleted.</returns>
    public static IReadOnlyList<string> ApplyRetention(string directory, DateTimeOffset now, TimeSpan maxAge, long maxTotalBytes)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(directory))
        {
            return deleted;
        }

        var files = new DirectoryInfo(directory)
            .EnumerateFiles(Prefix + "*" + Extension)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cutoff = now.UtcDateTime - maxAge;
        var kept = new List<FileInfo>();
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc < cutoff && TryDelete(file))
            {
                deleted.Add(file.FullName);
            }
            else
            {
                kept.Add(file);
            }
        }

        var total = kept.Sum(f => f.Length);
        foreach (var file in kept)
        {
            if (total <= maxTotalBytes)
            {
                break;
            }

            var length = file.Length;
            if (TryDelete(file))
            {
                deleted.Add(file.FullName);
                total -= length;
            }
        }

        return deleted;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
