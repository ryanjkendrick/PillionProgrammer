using System;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Remembers the last suggestion of a view so that, when the user types or accepts part of it (a word, or the first
/// lines of a multi-line suggestion), the rest is offered again at once without asking the host. A hit needs the text
/// between the suggestion's anchor and the caret to be exactly the start of the suggestion. Thread-safe.
/// </summary>
public sealed class ContinuationCache
{
    /// <summary>How many characters before the anchor are compared to detect that the document moved on.</summary>
    public const int TailLength = 64;

    private readonly object _gate = new();
    private Entry? _entry;

    /// <summary>Remembers a suggestion.</summary>
    /// <param name="documentKey">Identifies the document (its path or buffer identity).</param>
    /// <param name="text">The document text the suggestion was made for.</param>
    /// <param name="anchor">The caret position the suggestion is inserted at.</param>
    /// <param name="completion">The suggestion, with the document's line breaks.</param>
    /// <param name="attempt">The attempt that produced it.</param>
    public void Remember(string documentKey, IDocumentText text, int anchor, string completion, long attempt)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (string.IsNullOrEmpty(documentKey) || string.IsNullOrEmpty(completion))
        {
            return;
        }

        var start = Math.Max(0, anchor - TailLength);
        var tail = text.GetText(start, anchor - start);
        lock (_gate)
        {
            _entry = new Entry(documentKey, anchor, tail, completion, attempt);
        }
    }

    /// <summary>Forgets the remembered suggestion (for example after Esc).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entry = null;
        }
    }

    /// <summary>Gets the rest of the remembered suggestion when the document still matches it up to the caret.</summary>
    /// <param name="documentKey">Identifies the document.</param>
    /// <param name="text">The current document text.</param>
    /// <param name="caret">The caret position.</param>
    /// <param name="remainder">The rest of the suggestion after the caret.</param>
    /// <param name="attempt">The attempt that produced the suggestion.</param>
    /// <returns><see langword="true"/> on a hit with a non-blank remainder.</returns>
    public bool TryGetRemainder(string documentKey, IDocumentText text, int caret, out string remainder, out long attempt)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        remainder = string.Empty;
        attempt = 0;
        Entry? entry;
        lock (_gate)
        {
            entry = _entry;
        }

        if (entry is null || !string.Equals(entry.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typed = caret - entry.Anchor;
        if (typed < 0 || typed >= entry.Completion.Length || caret > text.Length)
        {
            return false;
        }

        var start = entry.Anchor - entry.Tail.Length;
        if (start < 0)
        {
            return false;
        }

        var expected = entry.Tail + entry.Completion.Substring(0, typed);
        if (!string.Equals(text.GetText(start, expected.Length), expected, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = entry.Completion.Substring(typed);
        if (rest.Trim().Length == 0)
        {
            return false;
        }

        remainder = rest;
        attempt = entry.Attempt;
        return true;
    }

    private sealed class Entry(string documentKey, int anchor, string tail, string completion, long attempt)
    {
        public string DocumentKey { get; } = documentKey;

        public int Anchor { get; } = anchor;

        public string Tail { get; } = tail;

        public string Completion { get; } = completion;

        public long Attempt { get; } = attempt;
    }
}
