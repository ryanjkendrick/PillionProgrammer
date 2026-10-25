using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// What is happening in the IDE, as events and queries: builds, documents and diagnostics. One instance per devenv.exe,
/// owned by the package and registered as a Visual Studio service under this interface type (ask the global service
/// provider, <c>GetServiceAsync(typeof(IIdeSignals))</c>). Feeds <c>@errors</c> and the inline <c>/fix</c> (diagnostics),
/// and the phase 2 indexer (which pauses while <see cref="BuildActivity"/> says a build is running).
/// </summary>
/// <remarks>
/// Every event is raised on a thread-pool thread, one at a time and in the order the IDE reported them, so a handler
/// that is slow delays only later events, never the UI thread. A handler that throws is logged and skipped.
/// </remarks>
[Guid("7b2e4f1a-3c5d-4e8f-9a1b-6d0c2e7f8a93")]
public interface IIdeSignals
{
    /// <summary>Raised when a solution build, rebuild or clean begins.</summary>
    event EventHandler? BuildStarted;

    /// <summary>Raised when the build that <see cref="BuildStarted"/> announced ends, successfully or not.</summary>
    event EventHandler<BuildFinishedEventArgs>? BuildFinished;

    /// <summary>Raised when <see cref="Core.Signals.BuildActivity.IsBuilding"/> changes (the hook the phase 2 indexer uses to pause).</summary>
    event EventHandler<BuildStateChangedEventArgs>? BuildStateChanged;

    /// <summary>Raised when a document is opened in the IDE.</summary>
    event EventHandler<DocumentEventArgs>? DocumentOpened;

    /// <summary>Raised when a document is saved.</summary>
    event EventHandler<DocumentEventArgs>? DocumentSaved;

    /// <summary>Raised when a document is closed.</summary>
    event EventHandler<DocumentEventArgs>? DocumentClosed;

    /// <summary>Raised when another document becomes the active one; <see cref="DocumentEventArgs.Path"/> is <see langword="null"/> when none is.</summary>
    event EventHandler<DocumentEventArgs>? ActiveDocumentChanged;

    /// <summary>
    /// Raised, at most once per 500 ms of quiet, when the Error List's contents changed (builds, live IntelliSense
    /// diagnostics and Roslyn updates all reach the Error List). Call <see cref="GetDiagnosticsAsync"/> for the new list.
    /// The Error List is connected lazily (issue #49): this event starts firing only once something has asked for
    /// diagnostics (<see cref="GetDiagnosticsAsync"/> or <see cref="EnsureDiagnosticsConnectedAsync"/>).
    /// </summary>
    event EventHandler? DiagnosticsChanged;

    /// <summary>Gets whether a build is running now.</summary>
    BuildActivity BuildActivity { get; }

    /// <summary>Gets the full path of the active document, or <see langword="null"/> when there is none.</summary>
    string? ActiveDocumentPath { get; }

    /// <summary>
    /// Gets what the diagnostics can and cannot tell: that Error List entries are only the ones the developer's filters
    /// show, and whether Roslyn diagnostics are available. Empty until the signals have started.
    /// </summary>
    string DiagnosticsNote { get; }

    /// <summary>Gets the last 200 signal events with their times, oldest first (for diagnostics).</summary>
    IReadOnlyList<IdeSignalEvent> RecentEvents { get; }

    /// <summary>
    /// Connects the Error List now, if it is not connected yet (issue #49: it is connected on the first diagnostics query,
    /// not at package load, because Visual Studio creates the Error List's table control on first use, which cost 50 to
    /// 120 ms of UI-thread time). Call it before subscribing to <see cref="DiagnosticsChanged"/>, or when a later read
    /// must not pay for the connection (the self-healing pipeline reads the Error List when a build finishes). Idempotent;
    /// a failed connection is logged and leaves the diagnostics to Roslyn, and the next call tries again.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the connection.</param>
    /// <returns>A task that completes when the Error List is connected (or could not be).</returns>
    Task EnsureDiagnosticsConnectedAsync(CancellationToken cancellationToken);

    /// <summary>Gets the diagnostics for a scope. Connects the Error List first when it is not connected yet.</summary>
    /// <param name="scope">The active document, or the whole solution.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>
    /// The diagnostics, errors first. For <see cref="DiagnosticScope.Solution"/> these are the Error List's entries, so
    /// they follow the developer's Error List filters (scope and the Build + IntelliSense setting). For
    /// <see cref="DiagnosticScope.ActiveDocument"/> they are Roslyn's compiler diagnostics for the open text (including
    /// unsaved edits) plus the Error List entries for that file that Roslyn did not report. Empty when there is none.
    /// </returns>
    Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(DiagnosticScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the text of an Output window pane, tail-truncated. The Output window is connected on the first call (issue
    /// #49), not at package load.
    /// </summary>
    /// <param name="paneName">The pane's name: <c>Build</c>, <c>Debug</c> or <c>Tests</c> (case-insensitive).</param>
    /// <param name="maxChars">The most characters to return (the end of the pane is kept); at least 1.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The text, or <see langword="null"/> when the pane does not exist (yet).</returns>
    Task<string?> GetOutputPaneTextAsync(string paneName, int maxChars, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the last solution build that finished since Visual Studio started: its outcome, and the tail of the Build pane
    /// captured when it finished (so <c>@build</c> still has it after a later debug session or a cleared pane), or
    /// <see langword="null"/> when no build has finished. Added for <c>@build</c> (work item 2.13).
    /// </summary>
    LastBuildRecord? LastBuild { get; }
}

/// <summary>The last build that finished (work item 2.13).</summary>
public sealed class LastBuildRecord
{
    /// <summary>Initializes a new instance of the <see cref="LastBuildRecord"/> class.</summary>
    /// <param name="success">Whether it succeeded.</param>
    /// <param name="projectCount">How many projects it built.</param>
    /// <param name="duration">How long it took.</param>
    /// <param name="finishedAt">When it finished (UTC).</param>
    /// <param name="output">The Build pane's tail when it finished, or <see langword="null"/> while it is being read or when there is no reader.</param>
    public LastBuildRecord(bool success, int projectCount, TimeSpan duration, DateTimeOffset finishedAt, string? output)
    {
        Success = success;
        ProjectCount = projectCount;
        Duration = duration;
        FinishedAt = finishedAt;
        Output = output;
    }

    /// <summary>Gets a value indicating whether the build succeeded.</summary>
    public bool Success { get; }

    /// <summary>Gets how many projects it built.</summary>
    public int ProjectCount { get; }

    /// <summary>Gets how long it took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets when it finished (UTC).</summary>
    public DateTimeOffset FinishedAt { get; }

    /// <summary>Gets the Build pane's tail captured when it finished, or <see langword="null"/>.</summary>
    public string? Output { get; }

    /// <summary>The summary line <c>@build</c> sends before the output, for example <c>Build failed: 3 projects in 12.4 s, finished 10:42:07</c>.</summary>
    /// <returns>The line.</returns>
    public string Summary() =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Build {0}: {1} project{2} in {3:0.0} s, finished {4:HH:mm:ss}",
            Success ? "succeeded" : "failed",
            ProjectCount,
            ProjectCount == 1 ? string.Empty : "s",
            Duration.TotalSeconds,
            FinishedAt.ToLocalTime());
}

/// <summary>Whether a build is running.</summary>
public sealed class BuildActivity
{
    /// <summary>Initializes a new instance of the <see cref="BuildActivity"/> class.</summary>
    /// <param name="isBuilding">Whether a build is running.</param>
    /// <param name="startedAt">When the running build began, or <see langword="null"/> when idle.</param>
    public BuildActivity(bool isBuilding, DateTimeOffset? startedAt)
    {
        IsBuilding = isBuilding;
        StartedAt = startedAt;
    }

    /// <summary>Gets whether a solution build, rebuild or clean is running.</summary>
    public bool IsBuilding { get; }

    /// <summary>Gets when the running build began, or <see langword="null"/> when none is running.</summary>
    public DateTimeOffset? StartedAt { get; }
}

/// <summary>Event data for <see cref="IIdeSignals.BuildFinished"/>.</summary>
public sealed class BuildFinishedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="BuildFinishedEventArgs"/> class.</summary>
    /// <param name="success">Whether the build succeeded.</param>
    /// <param name="projectCount">How many projects the build processed.</param>
    /// <param name="duration">How long the build took.</param>
    public BuildFinishedEventArgs(bool success, int projectCount, TimeSpan duration)
    {
        Success = success;
        ProjectCount = projectCount;
        Duration = duration;
    }

    /// <summary>Gets a value indicating whether the build succeeded.</summary>
    public bool Success { get; }

    /// <summary>Gets how many projects the build processed.</summary>
    public int ProjectCount { get; }

    /// <summary>Gets how long the build took.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>Event data for <see cref="IIdeSignals.BuildStateChanged"/>.</summary>
public sealed class BuildStateChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="BuildStateChangedEventArgs"/> class.</summary>
    /// <param name="isBuilding">Whether a build is running now.</param>
    public BuildStateChangedEventArgs(bool isBuilding) => IsBuilding = isBuilding;

    /// <summary>Gets a value indicating whether a build is running now.</summary>
    public bool IsBuilding { get; }
}

/// <summary>Event data for the document events.</summary>
public sealed class DocumentEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="DocumentEventArgs"/> class.</summary>
    /// <param name="path">The full path of the document, or <see langword="null"/> for "no active document".</param>
    public DocumentEventArgs(string? path) => Path = path;

    /// <summary>Gets the full path of the document, or <see langword="null"/> when there is none.</summary>
    public string? Path { get; }
}

/// <summary>One entry of the recent-signals ring buffer.</summary>
public sealed class IdeSignalEvent
{
    /// <summary>Initializes a new instance of the <see cref="IdeSignalEvent"/> class.</summary>
    /// <param name="timestamp">When the IDE reported it.</param>
    /// <param name="kind">The signal's name, for example <c>BuildStarted</c>.</param>
    /// <param name="detail">A short description (a path, a build result), or empty.</param>
    public IdeSignalEvent(DateTimeOffset timestamp, string kind, string detail)
    {
        Timestamp = timestamp;
        Kind = kind;
        Detail = detail;
    }

    /// <summary>Gets when the IDE reported the signal.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the signal's name.</summary>
    public string Kind { get; }

    /// <summary>Gets a short description, or empty.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + Kind + (Detail.Length == 0 ? string.Empty : " " + Detail);
}

/// <summary>Reads the Error List.</summary>
public interface IErrorListSource
{
    /// <summary>Reads every entry the Error List currently shows.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The diagnostics (<see cref="DiagnosticInfo.Source"/> is <see cref="DiagnosticSource.ErrorList"/>).</returns>
    Task<IReadOnlyList<DiagnosticInfo>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads compiler diagnostics from the Roslyn workspace.</summary>
public interface IRoslynDiagnosticsSource
{
    /// <summary>Gets why Roslyn diagnostics are unavailable (the workspace is not loaded), or <see langword="null"/> when they are available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Reads the diagnostics for one open document.</summary>
    /// <param name="path">The document's full path.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The diagnostics, or <see langword="null"/> when Roslyn does not know the document (not C# or Visual Basic, or not in the workspace).</returns>
    Task<IReadOnlyList<DiagnosticInfo>?> ReadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads Output window panes.</summary>
public interface IOutputPaneSource
{
    /// <summary>Reads the end of a pane's text.</summary>
    /// <param name="paneName">The pane's name (<c>Build</c>, <c>Debug</c>, <c>Tests</c>).</param>
    /// <param name="maxChars">About how many characters from the end the caller needs; the source may return more.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The text, or <see langword="null"/> when the pane does not exist.</returns>
    Task<string?> ReadTailAsync(string paneName, int maxChars, CancellationToken cancellationToken);
}
