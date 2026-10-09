using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>One recent edit: the file, the zero-based line and the line's text after the edit.</summary>
/// <param name="filePath">The full path of the file.</param>
/// <param name="line">The zero-based line number.</param>
/// <param name="text">The changed line's text after the edit, trimmed and clipped.</param>
public sealed class RecentEdit(string filePath, int line, string text)
{
    /// <summary>Gets the full path of the file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Gets the zero-based line number.</summary>
    public int Line { get; } = line;

    /// <summary>Gets the changed line's text after the edit, trimmed and clipped.</summary>
    public string Text { get; } = text;

    /// <summary>Formats the edit for <c>CompletionRequest.RecentEdits</c>: <c>path:line: text</c> with a one-based line.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        FilePath + ":" + (Line + 1).ToString(CultureInfo.InvariantCulture) + ": " + Text;
}

/// <summary>
/// Keeps the last five edits across all documents for the completion request's intent signals. Consecutive edits to
/// the same line of the same file (typing a word) are merged into one entry that holds the line's latest text, so
/// the list shows five distinct places. Recording is cheap (it runs on the UI thread for every text change).
/// Thread-safe.
/// </summary>
public sealed class EditTracker
{
    /// <summary>How many edits are kept.</summary>
    public const int Capacity = 5;

    /// <summary>The longest line text kept per edit.</summary>
    public const int MaxTextLength = 160;

    private readonly object _gate = new();
    private readonly LinkedList<RecentEdit> _edits = new();

    /// <summary>Records an edit.</summary>
    /// <param name="filePath">The full path of the file.</param>
    /// <param name="line">The zero-based line number.</param>
    /// <param name="lineText">The line's text after the edit.</param>
    public void Record(string? filePath, int line, string? lineText)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        var text = Clip(lineText);
        var edit = new RecentEdit(filePath!, line, text);
        lock (_gate)
        {
            if (_edits.First is { } newest
                && newest.Value.Line == line
                && string.Equals(newest.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                newest.Value = edit;
                return;
            }

            // A return to a line edited earlier moves it to the front instead of listing it twice.
            for (var node = _edits.First; node is not null; node = node.Next)
            {
                if (node.Value.Line == line && string.Equals(node.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    _edits.Remove(node);
                    break;
                }
            }

            _edits.AddFirst(edit);
            while (_edits.Count > Capacity)
            {
                _edits.RemoveLast();
            }
        }
    }

    /// <summary>Gets the recent edits, newest first.</summary>
    /// <returns>The edits.</returns>
    public IReadOnlyList<RecentEdit> Snapshot()
    {
        lock (_gate)
        {
            return [.. _edits];
        }
    }

    /// <summary>Gets the recent edits formatted for <c>CompletionRequest.RecentEdits</c>, newest first.</summary>
    /// <returns>The formatted edits.</returns>
    public string[] ToRequestStrings()
    {
        var edits = Snapshot();
        var result = new string[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            result[i] = edits[i].ToString();
        }

        return result;
    }

    /// <summary>Forgets the edits of a file (for example when it is renamed or closed without saving).</summary>
    /// <param name="filePath">The full path.</param>
    public void Forget(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        lock (_gate)
        {
            for (var node = _edits.First; node is not null;)
            {
                var next = node.Next;
                if (string.Equals(node.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    _edits.Remove(node);
                }

                node = next;
            }
        }
    }

    private static string Clip(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed.Substring(0, MaxTextLength);
    }
}
