using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>What the editor supplies for one completion attempt. Everything here is safe to read off the UI thread.</summary>
public sealed class CompletionInput
{
    /// <summary>Gets or sets a key that identifies the document (its path, or a buffer identity for unsaved files).</summary>
    public string DocumentKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of the document, or an empty string.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the language identifier (as in <see cref="DocumentLanguage"/>).</summary>
    public string LanguageId { get; set; } = DocumentLanguage.PlainText;

    /// <summary>Gets or sets the immutable document text the caret refers to.</summary>
    public IDocumentText? Text { get; set; }

    /// <summary>Gets or sets the version of <see cref="Text"/> (the editor snapshot's version number).</summary>
    public long TextVersion { get; set; }

    /// <summary>Gets or sets the caret position.</summary>
    public int Caret { get; set; }

    /// <summary>Gets or sets the caret's virtual spaces (0 when not in virtual space).</summary>
    public int VirtualSpaces { get; set; }

    /// <summary>Gets or sets a value indicating whether to skip the debounce (the request that follows an accept).</summary>
    public bool Immediate { get; set; }

    /// <summary>Gets or sets a value indicating whether to ask for a multi-line suggestion regardless of the decision table.</summary>
    public bool ForceMultiLine { get; set; }

    /// <summary>
    /// Gets or sets a lookup of the enclosing declaration through a language service (Roslyn for C# and Visual Basic),
    /// started with the debounce and given a short budget after it; <see langword="null"/> uses the line heuristic.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? DeclarationProvider { get; set; }
}

/// <summary>A shaped suggestion (or the start of one) produced by an attempt.</summary>
public sealed class CompletionResult
{
    /// <summary>Gets or sets the attempt number.</summary>
    public long Attempt { get; set; }

    /// <summary>Gets or sets the request id sent to the host.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the suggestion text with <c>\n</c> line breaks, relative to the caret.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the request was multi-line.</summary>
    public bool IsMultiLine { get; set; }

    /// <summary>Gets or sets why the request was single-line or multi-line.</summary>
    public MultiLineReason MultiLineReason { get; set; }

    /// <summary>Gets or sets why the shaper stopped (<see cref="CompletionStopReason.None"/> for a partial result).</summary>
    public CompletionStopReason StopReason { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the whole suggestion (false for the first lines of one still streaming).</summary>
    public bool IsComplete { get; set; }

    /// <summary>Gets or sets the time from sending the request to this result.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Gets or sets the model that answered, when the host said.</summary>
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the provider that answered, when the host said.</summary>
    public string? ProviderId { get; set; }
}

/// <summary>
/// One completion attempt: the debounce, the request and the streamed answer. <see cref="Ready"/> completes with the
/// first text worth showing: for a single-line request the first chunk with visible text, for a multi-line one its
/// first complete line (design decision of 2026-10-11, Gate 1 open issue 4: "time to suggestion" is the first visible
/// text). While the rest streams, <see cref="Updated"/> reports the grown suggestion, at most once per
/// <see cref="AutocompleteEngine.DisplayUpdateInterval"/>; <see cref="Completed"/> completes with the whole suggestion.
/// Both tasks complete with <see langword="null"/> when there is nothing to show: cancelled, superseded by a newer
/// attempt, suppressed, empty or failed. Neither ever faults, and no update follows <see cref="Completed"/>.
/// </summary>
public sealed class CompletionAttempt
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<CompletionResult?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CompletionResult?> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private CompletionResult? _latest;
    private bool _shownEarly;

    internal CompletionAttempt(long id, string requestId, CancellationTokenSource cancellation)
    {
        Id = id;
        RequestId = requestId;
        _cancellation = cancellation;
    }

    /// <summary>Gets the attempt number (unique in the process).</summary>
    public long Id { get; }

    /// <summary>Gets the request id sent to the host.</summary>
    public string RequestId { get; }

    /// <summary>Gets a task that completes with the first suggestion worth showing, or <see langword="null"/>.</summary>
    public Task<CompletionResult?> Ready => _ready.Task;

    /// <summary>Gets a task that completes with the whole suggestion, or <see langword="null"/>.</summary>
    public Task<CompletionResult?> Completed => _completed.Task;

    /// <summary>
    /// Raised on a thread-pool thread each time the suggestion shown early has grown (never before <see cref="Ready"/>
    /// and never after <see cref="Completed"/>); the argument is the suggestion so far. Read <see cref="Latest"/> after
    /// subscribing so an update raised in between is not missed.
    /// </summary>
    public event EventHandler<CompletionResult>? Updated;

    /// <summary>Gets the newest suggestion handed out (by <see cref="Ready"/>, <see cref="Updated"/> or <see cref="Completed"/>), or <see langword="null"/>.</summary>
    public CompletionResult? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Gets the token that is cancelled when the attempt is cancelled or superseded.</summary>
    internal CancellationToken Token => _cancellation.Token;

    /// <summary>Gets a value indicating whether the attempt was cancelled or superseded.</summary>
    public bool IsCancelled => _cancellation.IsCancellationRequested;

    /// <summary>Cancels the attempt: the debounce or the stream stops and both tasks complete with <see langword="null"/>.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    internal void SetReady(CompletionResult? result)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted)
            {
                return;
            }

            _latest = result;
            _shownEarly = result is not null;
            _ready.TrySetResult(result);
        }
    }

    /// <summary>Reports the grown suggestion; ignored before <see cref="Ready"/> and after <see cref="Completed"/>.</summary>
    /// <returns><see langword="true"/> when the update was raised.</returns>
    internal bool Publish(CompletionResult update)
    {
        lock (_gate)
        {
            if (!_shownEarly || _completed.Task.IsCompleted)
            {
                return false;
            }

            _latest = update;
        }

        Updated?.Invoke(this, update);
        return true;
    }

    internal void SetCompleted(CompletionResult? result)
    {
        lock (_gate)
        {
            _latest = result;
            _ready.TrySetResult(result);
            _completed.TrySetResult(result);
        }
    }
}
