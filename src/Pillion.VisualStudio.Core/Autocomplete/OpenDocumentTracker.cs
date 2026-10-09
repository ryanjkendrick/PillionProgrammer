using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The open documents in most-recently-used order, fed by editor view focus and close events so a completion request
/// can list them without touching the running document table (a UI-thread object). Thread-safe.
/// </summary>
public sealed class OpenDocumentTracker
{
    /// <summary>The most documents listed in a request.</summary>
    public const int MaxListed = 20;

    private readonly object _gate = new();
    private readonly LinkedList<string> _documents = new();
    private readonly Dictionary<string, int> _viewCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes that a view of a document opened.</summary>
    /// <param name="filePath">The full path.</param>
    public void Opened(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        lock (_gate)
        {
            _viewCounts[filePath!] = _viewCounts.TryGetValue(filePath!, out var count) ? count + 1 : 1;
            MoveToFront(filePath!);
        }
    }

    /// <summary>Notes that a document's view got focus (or was edited): it becomes the most recently used.</summary>
    /// <param name="filePath">The full path.</param>
    public void Activated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        lock (_gate)
        {
            if (_viewCounts.ContainsKey(filePath!))
            {
                MoveToFront(filePath!);
            }
        }
    }

    /// <summary>Notes that a view of a document closed; the document leaves the list when its last view closes.</summary>
    /// <param name="filePath">The full path.</param>
    public void Closed(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        lock (_gate)
        {
            if (!_viewCounts.TryGetValue(filePath!, out var count))
            {
                return;
            }

            if (count > 1)
            {
                _viewCounts[filePath!] = count - 1;
                return;
            }

            _viewCounts.Remove(filePath!);
            Remove(filePath!);
        }
    }

    /// <summary>Gets the open documents, most recently used first.</summary>
    /// <returns>The full paths (at most <see cref="MaxListed"/>).</returns>
    public string[] Snapshot()
    {
        lock (_gate)
        {
            var result = new List<string>(Math.Min(_documents.Count, MaxListed));
            foreach (var path in _documents)
            {
                if (result.Count == MaxListed)
                {
                    break;
                }

                result.Add(path);
            }

            return [.. result];
        }
    }

    private void MoveToFront(string filePath)
    {
        if (_documents.First is { } first && string.Equals(first.Value, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Remove(filePath);
        _documents.AddFirst(filePath);
    }

    private void Remove(string filePath)
    {
        for (var node = _documents.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _documents.Remove(node);
                return;
            }
        }
    }
}
