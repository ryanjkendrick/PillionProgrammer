using System.Text;
using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>
/// The reversible redaction for one model request. Secrets in outbound text are replaced by placeholders
/// (<c>&lt;redacted:aws-access-key&gt;</c>, or <c>&lt;redacted:aws-access-key#2&gt;</c> once a request holds more than one
/// distinct value of a kind); originals are restored in model output before an edit is applied.
/// </summary>
/// <remarks>
/// <para>
/// Use one session per request and dispose it when the request ends. Originals are held in memory only and the
/// maps are cleared on <see cref="Dispose"/>. .NET strings are immutable, so the original characters are released
/// to the garbage collector rather than overwritten.
/// </para>
/// <para>
/// Numbering: the first distinct value of a kind gets <c>&lt;redacted:kind&gt;</c> if it is the only one when it is
/// first redacted, so text already handed to the model never has to change. Once a second distinct value appears,
/// new values get <c>#2</c>, <c>#3</c> and so on, and the unnumbered placeholder keeps meaning the first value.
/// When several distinct values are first seen in the same <see cref="Redact"/> call they are numbered from <c>#1</c>.
/// </para>
/// <para>
/// A placeholder is only meaningful within the session that issued it. Placeholders carry no session identifier, so
/// the same text can map to different originals in different sessions; never restore with another request's session.
/// </para>
/// <para>This type is thread-safe.</para>
/// </remarks>
public sealed class RedactionSession : IDisposable
{
    private const string PlaceholderPrefix = "<redacted:";

    // Anything placeholder-shaped, including malformed tokens a model might invent. Restore resolves the ones this
    // session issued; FindUnresolved reports the rest.
    private static readonly Regex PlaceholderShape = new(
        @"<redacted:[^<>\r\n]{0,80}>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        SecretScanner.MatchTimeout);

    private readonly object _gate = new();
    private readonly Dictionary<(SecretKind Kind, string Original), string> _placeholderByValue = [];
    private readonly Dictionary<string, string> _originalByPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<SecretKind, int> _distinctPerKind = [];
    private readonly List<string> _kindsInOrder = [];
    private readonly Dictionary<string, int> _countsByKind = new(StringComparer.Ordinal);
    private int _count;
    private int _distinctTotal;
    private bool _disposed;

    /// <summary>Gets what has been redacted so far (counts and kind ids only, never the originals).</summary>
    /// <remarks>Still available after <see cref="Dispose"/>.</remarks>
    public RedactionSummary Redactions
    {
        get
        {
            lock (_gate)
            {
                return new RedactionSummary(
                    _count,
                    _distinctTotal,
                    [.. _kindsInOrder],
                    new Dictionary<string, int>(_countsByKind, StringComparer.Ordinal));
            }
        }
    }

    /// <summary>Replaces every secret in <paramref name="text"/> with its placeholder.</summary>
    /// <param name="text">Text bound for a cloud model, a log, the audit trail or an index.</param>
    /// <returns>
    /// The text with each secret replaced; the same original always gets the same placeholder within this session.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="RegexMatchTimeoutException">
    /// Scanning timed out; do not send the text (see <see cref="SecretScanner.Scan"/>).
    /// </exception>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            var matches = SecretScanner.Scan(text);
            if (matches.Count == 0)
            {
                return text;
            }

            // Pass 1: register new distinct values so the numbering decision sees all of them.
            var added = new List<(SecretKind Kind, string Original, int Index)>();
            var addedKeys = new HashSet<(SecretKind Kind, string Original)>();
            foreach (var match in matches)
            {
                var original = text.Substring(match.Start, match.Length);
                if (_placeholderByValue.ContainsKey((match.Kind, original)) || !addedKeys.Add((match.Kind, original)))
                {
                    continue;
                }

                _distinctPerKind.TryGetValue(match.Kind, out var seen);
                _distinctPerKind[match.Kind] = ++seen;
                _distinctTotal++;
                added.Add((match.Kind, original, seen));
            }

            // Pass 2: choose placeholders for the new values.
            foreach (var (kind, original, index) in added)
            {
                var id = kind.ToId();
                var single = _distinctPerKind[kind] == 1;
                var placeholder = single ? $"{PlaceholderPrefix}{id}>" : $"{PlaceholderPrefix}{id}#{index}>";
                _placeholderByValue[(kind, original)] = placeholder;
                _originalByPlaceholder[placeholder] = original;
                if (single)
                {
                    // Tolerate a model writing the explicit form for the first value.
                    _originalByPlaceholder[$"{PlaceholderPrefix}{id}#1>"] = original;
                }
            }

            // Pass 3: splice.
            var builder = new StringBuilder(text.Length);
            var position = 0;
            foreach (var match in matches)
            {
                builder.Append(text, position, match.Start - position);
                builder.Append(_placeholderByValue[(match.Kind, text.Substring(match.Start, match.Length))]);
                position = match.End;

                var id = match.Kind.ToId();
                if (!_countsByKind.TryGetValue(id, out var perKind))
                {
                    _kindsInOrder.Add(id);
                }

                _countsByKind[id] = perKind + 1;
                _count++;
            }

            builder.Append(text, position, text.Length - position);
            return builder.ToString();
        }
    }

    /// <summary>Puts the originals back in place of the placeholders this session issued.</summary>
    /// <param name="text">Model output (or any text containing placeholders).</param>
    /// <returns>
    /// The text with known placeholders replaced by their originals. Unknown placeholder-shaped tokens are left as
    /// they are; use <see cref="FindUnresolved"/> to detect them.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public string Restore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (text.IndexOf(PlaceholderPrefix, StringComparison.Ordinal) < 0 || _originalByPlaceholder.Count == 0)
            {
                return text;
            }

            // A single pass, so an original that itself looks like a placeholder is never expanded twice.
            return PlaceholderShape.Replace(
                text,
                m => _originalByPlaceholder.TryGetValue(m.Value, out var original) ? original : m.Value);
        }
    }

    /// <summary>
    /// Finds placeholder-shaped tokens (<c>&lt;redacted:...&gt;</c>) in <paramref name="text"/> that this session did not
    /// issue. A gateway should reject an edit that contains any.
    /// </summary>
    /// <param name="text">Model output or a proposed edit.</param>
    /// <returns>The distinct unknown tokens in order of first appearance; empty when there are none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public IReadOnlyList<string> FindUnresolved(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (text.IndexOf(PlaceholderPrefix, StringComparison.Ordinal) < 0)
            {
                return [];
            }

            var unresolved = new List<string>();
            foreach (Match match in PlaceholderShape.Matches(text))
            {
                if (!_originalByPlaceholder.ContainsKey(match.Value) && !unresolved.Contains(match.Value))
                {
                    unresolved.Add(match.Value);
                }
            }

            return unresolved;
        }
    }

    /// <summary>Clears the originals and placeholder maps. Counts remain readable through <see cref="Redactions"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _placeholderByValue.Clear();
            _originalByPlaceholder.Clear();
            _distinctPerKind.Clear();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
