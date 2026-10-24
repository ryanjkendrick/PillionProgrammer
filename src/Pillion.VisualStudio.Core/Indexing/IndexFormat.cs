using System;
using System.Globalization;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Turns the index status the host reports (contract 1.3) into text for the Index window, the status bar indicator and
/// screen readers. All wording comes from <see cref="IndexStrings"/>; nothing here touches Visual Studio.
/// </summary>
public static class IndexFormat
{
    /// <summary>The glyph before the status bar text while indexing runs.</summary>
    public const string IndexingGlyph = "↻";

    /// <summary>The glyph before the status bar text while indexing is paused.</summary>
    public const string PausedGlyph = "‖";

    /// <summary>The glyph before the status bar text after an indexing error.</summary>
    public const string FailedGlyph = "⚠";

    /// <summary>Names an indexing phase.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns>The name, for example "Embedding chunks".</returns>
    public static string Phase(IndexPhase phase) => phase switch
    {
        IndexPhase.Idle => IndexStrings.PhaseIdle,
        IndexPhase.Discovering => IndexStrings.PhaseDiscovering,
        IndexPhase.Hashing => IndexStrings.PhaseHashing,
        IndexPhase.Chunking => IndexStrings.PhaseChunking,
        IndexPhase.Embedding => IndexStrings.PhaseEmbedding,
        IndexPhase.Storing => IndexStrings.PhaseStoring,
        IndexPhase.Paused => IndexStrings.PhasePaused,
        IndexPhase.Failed => IndexStrings.PhaseFailed,
        _ => phase.ToString(),
    };

    /// <summary>Says why indexing is paused, in a sentence fragment.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The text, for example "a build is running"; empty for <see cref="IndexPauseReason.None"/>.</returns>
    public static string PauseReason(IndexPauseReason reason) => reason switch
    {
        IndexPauseReason.None => string.Empty,
        IndexPauseReason.User => IndexStrings.PauseUser,
        IndexPauseReason.Build => IndexStrings.PauseBuild,
        IndexPauseReason.Test => IndexStrings.PauseTest,
        IndexPauseReason.Debug => IndexStrings.PauseDebug,
        IndexPauseReason.Battery => IndexStrings.PauseBattery,
        IndexPauseReason.Memory => IndexStrings.PauseMemory,
        IndexPauseReason.LowDisk => IndexStrings.PauseLowDisk,
        _ => reason.ToString(),
    };

    /// <summary>Says why indexing is paused in one or two words, for the status bar.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The text, for example "build"; empty for <see cref="IndexPauseReason.None"/>.</returns>
    public static string PauseReasonShort(IndexPauseReason reason) => reason switch
    {
        IndexPauseReason.None => string.Empty,
        IndexPauseReason.User => IndexStrings.PauseUserShort,
        IndexPauseReason.Build => IndexStrings.PauseBuildShort,
        IndexPauseReason.Test => IndexStrings.PauseTestShort,
        IndexPauseReason.Debug => IndexStrings.PauseDebugShort,
        IndexPauseReason.Battery => IndexStrings.PauseBatteryShort,
        IndexPauseReason.Memory => IndexStrings.PauseMemoryShort,
        IndexPauseReason.LowDisk => IndexStrings.PauseLowDiskShort,
        _ => reason.ToString(),
    };

    /// <summary>Names whether this host owns an index or reads it.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The name: writer, reader or "not open".</returns>
    public static string Role(IndexRole role) => role switch
    {
        IndexRole.Writer => IndexStrings.RoleWriter,
        IndexRole.Reader => IndexStrings.RoleReader,
        _ => IndexStrings.RoleUnknown,
    };

    /// <summary>Formats a count with the culture's digit grouping.</summary>
    /// <param name="value">The count.</param>
    /// <returns>The text.</returns>
    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Formats a size in bytes as KB, MB or GB (1,024 per step).</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The text, for example "4.2 MB".</returns>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return Math.Max(0, bytes).ToString("N0", CultureInfo.CurrentCulture) + " B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Formats the estimated time left.</summary>
    /// <param name="seconds">The estimate in seconds, or a negative number when there is none.</param>
    /// <returns>The text, for example "About 3 min remaining"; empty when there is no estimate.</returns>
    public static string Remaining(int seconds)
    {
        if (seconds < 0)
        {
            return string.Empty;
        }

        if (seconds < 60)
        {
            return IndexStrings.RemainingUnderMinute;
        }

        var minutes = (seconds + 30) / 60;
        return minutes < 60
            ? IndexStrings.Format(IndexStrings.RemainingMinutesFormat, minutes)
            : IndexStrings.Format(IndexStrings.RemainingHoursFormat, minutes / 60, minutes % 60);
    }

    /// <summary>Formats when the last full hash sweep finished.</summary>
    /// <param name="utc">The time in UTC, or <see langword="null"/> when no sweep has finished.</param>
    /// <returns>The local time, or "Never".</returns>
    public static string Moment(DateTime? utc) =>
        utc is { } value
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : IndexStrings.SweepNever;

    /// <summary>Names a rule that excluded a file, for the Excluded files list.</summary>
    /// <param name="reason">The <see cref="ExcludedFile.Reason"/> (a rule name).</param>
    /// <returns>The name; a rule this version does not know is shown as the host named it.</returns>
    public static string ExcludedReason(string? reason) => reason switch
    {
        "SecretBearing" => IndexStrings.ReasonSecretBearing,
        "GitIgnore" => IndexStrings.ReasonGitIgnore,
        "PillionIgnore" => IndexStrings.ReasonPillionIgnore,
        "BuiltIn" => IndexStrings.ReasonBuiltIn,
        "UserExclude" => IndexStrings.ReasonUserExclude,
        "GeneratedFile" => IndexStrings.ReasonGeneratedFile,
        "MinifiedFile" => IndexStrings.ReasonMinifiedFile,
        "LockFile" => IndexStrings.ReasonLockFile,
        "GeneratedHeader" => IndexStrings.ReasonGeneratedHeader,
        "Binary" => IndexStrings.ReasonBinary,
        "TooLarge" => IndexStrings.ReasonTooLarge,
        "ReparsePoint" => IndexStrings.ReasonReparsePoint,
        "NestedRepository" => IndexStrings.ReasonNestedRepository,
        "Unreadable" => IndexStrings.ReasonUnreadable,
        null or "" => IndexStrings.ReasonUnknown,
        _ => reason,
    };

    /// <summary>Says whether the indexer is working (a pipeline phase, or the host says it is indexing and not paused).</summary>
    /// <param name="status">The status.</param>
    /// <returns><see langword="true"/> while indexing runs.</returns>
    public static bool IsActive(IndexStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        return !IsPaused(status) && (status.IsIndexing || status.Phase is IndexPhase.Discovering or IndexPhase.Hashing or IndexPhase.Chunking or IndexPhase.Embedding or IndexPhase.Storing);
    }

    /// <summary>Says whether indexing is paused, for any reason.</summary>
    /// <param name="status">The status.</param>
    /// <returns><see langword="true"/> while paused.</returns>
    public static bool IsPaused(IndexStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        return status.IsPaused || status.Phase == IndexPhase.Paused;
    }

    /// <summary>Gets how far the run in progress has got, from 0 to 1.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The fraction, or <see langword="null"/> when there is nothing to measure against yet.</returns>
    public static double? Fraction(IndexStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        var (done, total) = status.Progress is { } progress
            ? (progress.FilesDone, progress.FilesTotal)
            : (status.IndexedFileCount, status.TotalFileCount);
        return total > 0 ? Math.Min(1.0, Math.Max(0.0, (double)done / total)) : null;
    }

    /// <summary>Formats the indexing indicator that follows the host's state in the status bar.</summary>
    /// <param name="status">The status, or <see langword="null"/> when none has been reported.</param>
    /// <returns>For example "↻ Indexing 42%", "‖ Index paused: build" or "⚠ Index error"; empty when idle.</returns>
    public static string StatusBarText(IndexStatus? status)
    {
        var (glyph, label) = StatusBarParts(status);
        return label.Length == 0 ? string.Empty : glyph + " " + label;
    }

    /// <summary>
    /// The two parts of <see cref="StatusBarText"/>: the glyph and the words. They are shown apart because the glyphs are not
    /// in Segoe UI, Visual Studio's interface font: text that holds them makes WPF look for a fallback font, and the first such
    /// lookup in a process parses the system's composite fonts on the UI thread (seconds on a CI runner). The clickable item
    /// draws the glyph in a font that has it; the plain status bar text (the fallback) shows the words only.
    /// </summary>
    /// <param name="status">The status, or <see langword="null"/> when none has been reported.</param>
    /// <returns>For example ("↻", "Indexing 42%"); two empty strings when idle.</returns>
    public static (string Glyph, string Label) StatusBarParts(IndexStatus? status)
    {
        if (status is null)
        {
            return (string.Empty, string.Empty);
        }

        if (status.Phase == IndexPhase.Failed)
        {
            return (FailedGlyph, IndexStrings.StatusBarFailed);
        }

        if (IsPaused(status))
        {
            var reason = PauseReasonShort(status.PauseReason);
            return (PausedGlyph, reason.Length == 0
                ? IndexStrings.StatusBarPaused
                : IndexStrings.Format(IndexStrings.StatusBarPausedFormat, reason));
        }

        if (IsActive(status))
        {
            return (IndexingGlyph, Fraction(status) is { } fraction
                ? IndexStrings.Format(IndexStrings.StatusBarIndexingFormat, (int)Math.Floor(fraction * 100))
                : IndexStrings.StatusBarIndexing);
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>Describes the whole status in one sentence, for the status bar tooltip and for screen readers.</summary>
    /// <param name="status">The status, or <see langword="null"/> when none has been reported.</param>
    /// <returns>For example "Indexing: Embedding chunks, 30 of 40 files (75%), about 1 min remaining".</returns>
    public static string Summary(IndexStatus? status)
    {
        if (status is null)
        {
            return IndexStrings.SummaryNoStatus;
        }

        if (status.Phase == IndexPhase.Failed)
        {
            return string.IsNullOrWhiteSpace(status.LastError)
                ? IndexStrings.SummaryFailed
                : IndexStrings.Format(IndexStrings.SummaryFailedFormat, status.LastError);
        }

        if (IsPaused(status))
        {
            var reason = PauseReason(status.PauseReason);
            return reason.Length == 0
                ? IndexStrings.SummaryPaused
                : IndexStrings.Format(IndexStrings.SummaryPausedFormat, reason);
        }

        if (IsActive(status))
        {
            var text = IndexStrings.Format(IndexStrings.SummaryIndexingFormat, Phase(status.Phase));
            if (status.Progress is { } progress && progress.FilesTotal > 0)
            {
                text += ", " + IndexStrings.Format(IndexStrings.ProgressFormat, Count(progress.FilesDone), Count(progress.FilesTotal), Count(progress.ChunksDone));
                if (Fraction(status) is { } fraction)
                {
                    text += " (" + IndexStrings.Format(IndexStrings.PercentFormat, (int)Math.Floor(fraction * 100)) + ")";
                }

                var remaining = Remaining(progress.EstimatedRemainingSeconds);
                if (remaining.Length > 0)
                {
                    text += ", " + remaining.ToLower(CultureInfo.CurrentCulture);
                }
            }

            return text;
        }

        return (status.Roots?.Length ?? 0) == 0 ? IndexStrings.SummaryNoIndex : IndexStrings.SummaryIdle;
    }
}
