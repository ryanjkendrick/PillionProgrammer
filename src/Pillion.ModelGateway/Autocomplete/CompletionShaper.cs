using System.Text;

namespace Pillion.ModelGateway;

/// <summary>
/// Applies the stop rules to a streamed completion on the gateway side, whatever the provider honoured: the text ends
/// before the first stop sequence, and after <c>maxLines</c> lines (one for a single-line request, so it ends before the
/// first newline; 30 for a multi-line one, whose block rules the client applies). Text that could be the start of a stop
/// sequence is held back until it is known not to be one. Not thread-safe; one per stream.
/// </summary>
public sealed class CompletionShaper
{
    private readonly string[] _stops;
    private readonly int _maxLines;
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _emitted = new();
    private int _newlines;

    /// <summary>Initializes a new instance of the <see cref="CompletionShaper"/> class.</summary>
    /// <param name="stopSequences">The stop sequences (empty ones are ignored).</param>
    /// <param name="maxLines">The most lines the completion may have (at least 1).</param>
    public CompletionShaper(IEnumerable<string> stopSequences, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(stopSequences);
        _stops = stopSequences.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
        _maxLines = Math.Max(1, maxLines);
    }

    /// <summary>Gets a value indicating whether a stop sequence or the line limit ended the completion; later pieces are ignored.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Gets everything emitted so far.</summary>
    public string Text => _emitted.ToString();

    /// <summary>Adds a streamed piece.</summary>
    /// <param name="piece">The piece.</param>
    /// <returns>The text that can be shown now (possibly empty).</returns>
    public string Push(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        if (IsComplete || piece.Length == 0)
        {
            return string.Empty;
        }

        _pending.Append(piece);
        var pending = _pending.ToString();

        var cut = -1;
        foreach (var stop in _stops)
        {
            var index = pending.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (cut < 0 || index < cut))
            {
                cut = index;
            }
        }

        var lines = _newlines;
        for (var i = 0; i < pending.Length && (cut < 0 || i < cut); i++)
        {
            if (pending[i] == '\n' && ++lines >= _maxLines)
            {
                cut = i;
                break;
            }
        }

        if (cut >= 0)
        {
            IsComplete = true;
            _pending.Clear();
            return Emit(pending[..cut]);
        }

        var hold = HeldBack(pending);
        _pending.Clear().Append(pending, pending.Length - hold, hold);
        return Emit(pending[..^hold]);
    }

    /// <summary>Ends the stream and returns the text that was held back.</summary>
    /// <returns>The rest of the completion.</returns>
    public string Flush()
    {
        if (IsComplete)
        {
            return string.Empty;
        }

        var rest = _pending.ToString();
        _pending.Clear();
        return Emit(rest);
    }

    private string Emit(string text)
    {
        foreach (var c in text)
        {
            if (c == '\n')
            {
                _newlines++;
            }
        }

        _emitted.Append(text);
        return text;
    }

    /// <summary>Gets the length of the longest tail of the text that is a proper prefix of a stop sequence.</summary>
    private int HeldBack(string text)
    {
        var hold = 0;
        foreach (var stop in _stops)
        {
            for (var length = Math.Min(stop.Length - 1, text.Length); length > hold; length--)
            {
                if (text.AsSpan(text.Length - length).SequenceEqual(stop.AsSpan(0, length)))
                {
                    hold = length;
                    break;
                }
            }
        }

        return hold;
    }
}
