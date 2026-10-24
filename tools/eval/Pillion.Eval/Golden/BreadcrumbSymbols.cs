using Pillion.ContextEngine.Chunking;

namespace Pillion.Eval.Golden;

/// <summary>
/// Matches expected symbols against chunk breadcrumbs (<c>path &gt; namespace &gt; type &gt; member</c>). The segments after
/// the path are split on <c>.</c> and <c>::</c>, with generic arguments (<c>Greeter&lt;T&gt;</c>), parameter lists and the
/// part marker removed; a symbol such as <c>OrderService.Place</c> matches when its parts appear as consecutive names, so
/// <c>Greeter</c> matches every member chunk of the type and <c>Greeter.Greet</c> only that member. Names compare ordinally.
/// </summary>
internal static class BreadcrumbSymbols
{
    /// <summary>Splits a symbol into its dotted parts.</summary>
    /// <param name="symbol">The symbol (<c>Type</c>, <c>Type.Member</c>, <c>Outer.Inner</c>, <c>ns::Type</c>).</param>
    /// <returns>The parts; empty for an empty symbol.</returns>
    public static IReadOnlyList<string> SplitSymbol(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol) ? [] : SplitName(symbol);

    /// <summary>Gets the names of a breadcrumb after its path, in order.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> Names(string? breadcrumb)
    {
        if (string.IsNullOrWhiteSpace(breadcrumb))
        {
            return [];
        }

        IReadOnlyList<string> segments;
        if (!Breadcrumbs.TryParse(breadcrumb, out segments, out _, out _))
        {
            segments = breadcrumb.Split(Breadcrumbs.Separator);
        }

        var names = new List<string>();
        foreach (var segment in segments.Skip(1))
        {
            names.AddRange(SplitName(segment));
        }

        return names;
    }

    /// <summary>Whether a breadcrumb contains a symbol as consecutive names.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <param name="symbol">The symbol.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool Contains(string? breadcrumb, string symbol) => Contains(Names(breadcrumb), SplitSymbol(symbol));

    /// <summary>Whether a list of names contains another as a consecutive run.</summary>
    /// <param name="names">The breadcrumb's names.</param>
    /// <param name="parts">The symbol's parts.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool Contains(IReadOnlyList<string> names, IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0 || parts.Count > names.Count)
        {
            return false;
        }

        for (var start = 0; start + parts.Count <= names.Count; start++)
        {
            var match = true;
            for (var i = 0; i < parts.Count && match; i++)
            {
                match = string.Equals(names[start + i], parts[i], StringComparison.Ordinal);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SplitName(string segment)
    {
        var text = segment.Trim();
        var marker = text.IndexOf(" (part ", StringComparison.Ordinal);
        if (marker >= 0)
        {
            text = text[..marker];
        }

        // Generic arguments and parameter lists may themselves contain dots, so they go before splitting.
        text = StripBracketed(StripBracketed(text, '<', '>'), '(', ')');
        return [.. text.Replace("::", ".", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static string StripBracketed(string text, char open, char close)
    {
        if (text.IndexOf(open, StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c == open)
            {
                depth++;
            }
            else if (c == close && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
