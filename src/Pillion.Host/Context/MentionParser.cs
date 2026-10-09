namespace Pillion.Host.Context;

/// <summary>A mention split into its kind and value.</summary>
/// <param name="Kind">The kind (<c>file</c>, <c>folder</c>, <c>selection</c>, <c>errors</c>, <c>clipboard</c>, or another known kind this version cannot resolve), or empty for a fuzzy name.</param>
/// <param name="Value">The part after <c>@kind:</c>, or the whole name for a fuzzy mention, without quotes.</param>
internal sealed record ParsedMention(string Kind, string Value);

/// <summary>
/// Parses the mention grammar of design section 7: <c>@</c> followed by a fuzzy name (<c>@Customer.cs</c>), or <c>@kind:value</c>
/// (<c>@file:src/Program.cs</c>, <c>@folder:src/Api</c>), or a bare <c>@selection</c>, <c>@errors</c> or <c>@clipboard</c>.
/// A colon only starts a value when the text before it is a known kind, so <c>@C:\src\Foo.cs</c> is one fuzzy name.
/// </summary>
internal static class MentionParser
{
    /// <summary>The kinds the grammar knows; a kind outside the first five resolves in a later phase.</summary>
    private static readonly string[] KnownKinds =
    [
        "file", "folder", "selection", "errors", "clipboard",
        "symbol", "docs", "terminal", "output", "build", "tests", "git", "solution",
    ];

    /// <summary>Parses a mention.</summary>
    /// <param name="mention">The mention text, with or without the leading <c>@</c>.</param>
    /// <param name="kindHint">The <see cref="Pillion.Contracts.MentionRequest.Kind"/> the client set; it wins over the text.</param>
    /// <returns>The kind and value.</returns>
    public static ParsedMention Parse(string? mention, string? kindHint)
    {
        var text = (mention ?? string.Empty).Trim();
        if (text.StartsWith('@'))
        {
            text = text[1..];
        }

        var kind = string.Empty;
        var value = text;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && IsKnown(text[..colon]))
        {
            kind = text[..colon].ToLowerInvariant();
            value = text[(colon + 1)..];
        }
        else if (IsKnown(text))
        {
            kind = text.ToLowerInvariant();
            value = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(kindHint))
        {
            kind = kindHint.Trim().ToLowerInvariant();
        }

        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return new ParsedMention(kind, value);
    }

    private static bool IsKnown(string text) => KnownKinds.Contains(text, StringComparer.OrdinalIgnoreCase);
}
