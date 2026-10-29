using System;
using System.Collections.Generic;
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
/// first text worth showing: for a single-line request the whole line, once it is complete; for a multi-line one its
/// first complete line (issue #51, decided 2026-10-24: Visual Studio's <c>TryUpdateProposal</c> does not change the
/// displayed ghost text, so what is shown first is what Tab takes, and a multi-line suggestion's remaining lines are
/// offered after the first is accepted, from the remembered suggestion). <see cref="Completed"/> completes with the whole
/// suggestion. Both tasks complete with <see langword="null"/> when there is nothing to show: cancelled, superseded by a
/// newer attempt, suppressed, empty or failed. Neither ever faults.
/// </summary>
public sealed class CompletionAttempt
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<CompletionResult?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CompletionResult?> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private int _typedThrough;
    private long _typedThroughVersion;

    internal CompletionAttempt(long id, string requestId, long textVersion, CancellationTokenSource cancellation)
    {
        Id = id;
        RequestId = requestId;
        _typedThroughVersion = textVersion;
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
    /// Gets how many characters the user typed at the caret, straight ahead, while this attempt was in flight (issue #52,
    /// option 3: typing that does not invalidate the prefix the attempt was made for keeps it alive). The editor compares
    /// them with the start of the suggestion when it arrives, and offers the remainder at the new caret when they match.
    /// </summary>
    public int TypedThroughLength => Volatile.Read(ref _typedThrough);

    /// <summary>Gets the text version the typed-through characters belong to (the request's version while nothing was typed).</summary>
    public long TypedThroughVersion => Interlocked.Read(ref _typedThroughVersion);

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
            _ready.TrySetResult(result);
        }
    }

    internal void SetCompleted(CompletionResult? result)
    {
        lock (_gate)
        {
            _ready.TrySetResult(result);
            _completed.TrySetResult(result);
        }
    }

    /// <summary>Records more typed-through characters (the session calls this under its own lock).</summary>
    internal void TypedThrough(int chars, long textVersion)
    {
        Interlocked.Exchange(ref _typedThroughVersion, textVersion);
        Interlocked.Add(ref _typedThrough, chars);
    }
}

/// <summary>One change of the document text, as the editor reports it: what was replaced and by how much.</summary>
public readonly struct TextEdit
{
    /// <summary>Initializes a new instance of the <see cref="TextEdit"/> struct.</summary>
    /// <param name="position">Where the change starts, in the text before the change.</param>
    /// <param name="oldLength">How many characters were replaced (0 for an insertion).</param>
    /// <param name="newLength">How many characters replaced them (0 for a deletion).</param>
    public TextEdit(int position, int oldLength, int newLength)
    {
        Position = position;
        OldLength = oldLength;
        NewLength = newLength;
    }

    /// <summary>Gets where the change starts, in the text before the change.</summary>
    public int Position { get; }

    /// <summary>Gets how many characters were replaced (0 for an insertion).</summary>
    public int OldLength { get; }

    /// <summary>Gets how many characters replaced them (0 for a deletion).</summary>
    public int NewLength { get; }
}

/// <summary>
/// The type-through rule of issue #52 (option 3, decided 2026-10-24): an attempt made for a caret stays alive while the
/// user types straight ahead at that caret, because the prefix it was made for is still the start of the text; a
/// deletion, an edit elsewhere, or a caret move invalidates it. When the suggestion arrives, the characters typed
/// meanwhile must be its start, and the rest is offered at the new caret.
/// </summary>
public static class TypeThrough
{
    /// <summary>Decides whether a text change keeps an attempt alive.</summary>
    /// <param name="edits">The edits of one change (the editor may batch several).</param>
    /// <param name="caret">The caret the attempt was made for, moved by what was typed through so far.</param>
    /// <returns>How many characters were typed at the caret, or <see langword="null"/> when the change invalidates the prefix.</returns>
    public static int? TypedAt(IReadOnlyList<TextEdit> edits, int caret)
    {
        if (edits is null || edits.Count != 1)
        {
            return null; // Several edits at once (a paste with formatting, a rename): not typing.
        }

        var edit = edits[0];
        return edit.OldLength == 0 && edit.NewLength > 0 && edit.Position == caret ? edit.NewLength : null;
    }

    /// <summary>Matches the typed-through text with a suggestion.</summary>
    /// <param name="suggestion">The suggestion, in the document's line breaks, as made for the original caret.</param>
    /// <param name="typed">The text the user typed at that caret while the attempt was in flight.</param>
    /// <param name="virtualSpaces">The original caret's virtual spaces: the editor turns them into real spaces with the first typed character, and the suggestion never repeats them.</param>
    /// <param name="remainder">The part of the suggestion left to offer at the new caret.</param>
    /// <returns><see langword="true"/> when the typed text is the start of the suggestion and something non-blank is left.</returns>
    public static bool TryGetRemainder(string suggestion, string typed, int virtualSpaces, out string remainder)
    {
        remainder = string.Empty;
        if (string.IsNullOrEmpty(suggestion) || typed is null)
        {
            return false;
        }

        var start = 0;
        while (start < typed.Length && start < virtualSpaces && typed[start] == ' ' && !suggestion.StartsWith(typed.Substring(0, start + 1), StringComparison.Ordinal))
        {
            start++;
        }

        var effective = start == 0 ? typed : typed.Substring(start);
        if (effective.Length >= suggestion.Length || !suggestion.StartsWith(effective, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = suggestion.Substring(effective.Length);
        if (rest.Trim().Length == 0)
        {
            return false;
        }

        remainder = rest;
        return true;
    }
}
