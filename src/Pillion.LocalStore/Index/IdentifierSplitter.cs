using System.Text;

namespace Pillion.LocalStore.Index;

/// <summary>
/// Identifier splitting for lexical search (phase 2 decision of 2026-10-15: done at write time into a second FTS5 column,
/// not in a custom tokenizer). A token is what FTS5's <c>unicode61</c> tokenizer with <c>tokenchars '_'</c> keeps
/// together: a run of letters, digits and underscores. It is split on underscores, on lower-to-upper case changes
/// (<c>getOrder</c>), before the last capital of an acronym followed by a lower-case letter (<c>HTTPServer</c>) and between
/// letters and digits (<c>utf8</c>), and the parts are lower-cased.
/// </summary>
public static class IdentifierSplitter
{
    /// <summary>The most query tokens turned into the match expression; the rest are ignored.</summary>
    public const int MaxQueryTokens = 32;

    /// <summary>Splits one identifier into its lower-cased parts.</summary>
    /// <param name="identifier">The identifier, for example <c>getOrderTotal</c>, <c>get_order_total</c> or <c>XMLHttpRequest2</c>.</param>
    /// <returns>The parts, for example <c>get</c>, <c>order</c>, <c>total</c>; empty when it has no letters or digits.</returns>
    public static IReadOnlyList<string> Split(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var parts = new List<string>();
        AddParts(identifier.AsSpan(), parts);
        return parts;
    }

    /// <summary>
    /// Builds the <c>identifiers</c> column of a chunk: for every token of <paramref name="text"/> whose parts differ from
    /// the token itself (it has several parts, or leading underscores), its parts in order, separated by spaces. Tokens
    /// that are already a single lower-case word are left to the <c>text</c> column, which holds them as they are.
    /// </summary>
    /// <param name="text">The breadcrumb and chunk text.</param>
    /// <returns>The column value; empty when no token splits.</returns>
    public static string BuildIdentifiersColumn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length / 2);
        var parts = new List<string>(8);
        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            if (!IsTokenChar(span[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && IsTokenChar(span[i]))
            {
                i++;
            }

            var token = span[start..i];
            parts.Clear();
            AddParts(token, parts);
            if (parts.Count == 0 || (parts.Count == 1 && IsSameLowerCase(token, parts[0])))
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(part);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds an FTS5 match expression for a user or model query. Each token matches either as a whole (in the <c>text</c>
    /// column, where <c>get_order_total</c> is one token) or, when it splits, as the phrase of its parts (so
    /// <c>getOrderTotal</c> also matches <c>get_order_total</c> and the words <c>order total</c> match <c>getOrderTotal</c>
    /// through the <c>identifiers</c> column). Tokens are combined with <c>OR</c> and ranked by BM25. The last token is a
    /// prefix when <paramref name="prefixLastToken"/> is set and it is at least two characters long. Everything that is
    /// not a token character is dropped, so FTS5 syntax in the query cannot reach the parser.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="prefixLastToken">Whether the last token matches as a prefix (search as you type).</param>
    /// <returns>The match expression, or <see langword="null"/> when the query has no tokens.</returns>
    public static string? BuildMatchQuery(string query, bool prefixLastToken = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tokens = new List<string>();
        var span = query.AsSpan();
        var i = 0;
        while (i < span.Length && tokens.Count < MaxQueryTokens)
        {
            if (!IsTokenChar(span[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && IsTokenChar(span[i]))
            {
                i++;
            }

            tokens.Add(span[start..i].ToString());
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        var expressions = new List<string>(tokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>(8);
        for (var t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            var whole = token.ToLowerInvariant();
            var prefix = prefixLastToken && t == tokens.Count - 1 && token.Length >= 2;
            var suffix = prefix ? " *" : string.Empty;

            parts.Clear();
            AddParts(token.AsSpan(), parts);
            var expression = parts.Count == 0 || (parts.Count == 1 && parts[0] == whole)
                ? Quote(whole) + suffix
                : "(" + Quote(whole) + suffix + " OR " + Quote(string.Join(' ', parts)) + suffix + ")";

            if (seen.Add(expression))
            {
                expressions.Add(expression);
            }
        }

        return string.Join(" OR ", expressions);
    }

    /// <summary>Whether FTS5's <c>unicode61</c> tokenizer with <c>tokenchars '_'</c> keeps the character inside a token (approximately: letters, digits and underscore).</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for a token character.</returns>
    internal static bool IsTokenChar(char c) => c == '_' || char.IsLetterOrDigit(c) || char.IsSurrogate(c);

    private static string Quote(string phrase) => "\"" + phrase + "\"";

    private static bool IsSameLowerCase(ReadOnlySpan<char> token, string lower)
    {
        if (token.Length != lower.Length)
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            if (char.ToLowerInvariant(token[i]) != lower[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void AddParts(ReadOnlySpan<char> token, List<string> parts)
    {
        var i = 0;
        while (i < token.Length)
        {
            // Segments between underscores (and any other non-token character).
            if (token[i] == '_' || !IsTokenChar(token[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < token.Length && token[i] != '_' && IsTokenChar(token[i]))
            {
                i++;
            }

            AddCaseParts(token[start..i], parts);
        }
    }

    private static void AddCaseParts(ReadOnlySpan<char> segment, List<string> parts)
    {
        var start = 0;
        for (var i = 1; i < segment.Length; i++)
        {
            var previous = segment[i - 1];
            var current = segment[i];
            var boundary =
                (char.IsLower(previous) && char.IsUpper(current))
                || (char.IsLetter(previous) && char.IsDigit(current))
                || (char.IsDigit(previous) && char.IsLetter(current))
                || (char.IsUpper(previous) && char.IsUpper(current) && i + 1 < segment.Length && char.IsLower(segment[i + 1]));
            if (boundary)
            {
                parts.Add(segment[start..i].ToString().ToLowerInvariant());
                start = i;
            }
        }

        if (start < segment.Length)
        {
            parts.Add(segment[start..].ToString().ToLowerInvariant());
        }
    }
}
