using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>The mention being typed at the caret: where its <c>@</c> is and what follows it up to the caret.</summary>
public sealed class MentionToken
{
    /// <summary>Initializes a new instance of the <see cref="MentionToken"/> class.</summary>
    /// <param name="start">The index of the <c>@</c> in the input.</param>
    /// <param name="length">The length of the mention, from the <c>@</c> to the end of the word it is in.</param>
    /// <param name="query">The text between the <c>@</c> and the caret.</param>
    public MentionToken(int start, int length, string query)
    {
        Start = start;
        Length = length;
        Query = query ?? string.Empty;
        Parsed = MentionSyntax.Parse("@" + Query);
    }

    /// <summary>Gets the index of the <c>@</c> in the input.</summary>
    public int Start { get; }

    /// <summary>Gets the length of the mention, from the <c>@</c> to the end of the word it is in (past the caret too).</summary>
    public int Length { get; }

    /// <summary>Gets the text between the <c>@</c> and the caret, for example <c>Prog</c> or <c>file:src/Ap</c>.</summary>
    public string Query { get; }

    /// <summary>Gets the query read with the mention grammar.</summary>
    public ParsedMention Parsed { get; }
}

/// <summary>A mention read with the grammar <c>@name</c> or <c>@kind:value</c>.</summary>
public sealed class ParsedMention
{
    /// <summary>Initializes a new instance of the <see cref="ParsedMention"/> class.</summary>
    /// <param name="kind">One of <see cref="MentionSyntax.Kinds"/>, or empty for a bare name.</param>
    /// <param name="value">The value after the kind, or the bare name.</param>
    /// <param name="hasKindSeparator">Whether the text had <c>kind:</c> (so the user chose the kind).</param>
    public ParsedMention(string kind, string value, bool hasKindSeparator)
    {
        Kind = kind;
        Value = value;
        HasKindSeparator = hasKindSeparator;
    }

    /// <summary>Gets the kind, or empty for a bare <c>@name</c> (a fuzzy file match, else a folder).</summary>
    public string Kind { get; }

    /// <summary>Gets the value: the name after <c>kind:</c>, or the bare name.</summary>
    public string Value { get; }

    /// <summary>Gets a value indicating whether the text named its kind with <c>kind:</c>.</summary>
    public bool HasKindSeparator { get; }
}

/// <summary>
/// The <c>@</c>-mention grammar of the chat input (design section 7, Context pinning): <c>@</c> followed by a fuzzy name
/// pins a file or folder (<c>@Customer.cs</c>); <c>@kind:value</c> disambiguates (<c>@folder:src/Api</c>,
/// <c>@symbol:OrderService</c>); the kinds without a value are <c>@selection</c>, <c>@errors</c>, <c>@clipboard</c>,
/// <c>@solution</c>, <c>@build</c> and <c>@tests</c>, and <c>@git</c> and <c>@output</c> take an optional one
/// (<c>@git:staged</c>, <c>@git:log:5</c>, <c>@output:debug</c>). Work item 2.13 added the kinds after <c>@clipboard</c>.
/// </summary>
public static class MentionSyntax
{
    /// <summary>The <c>file</c> kind.</summary>
    public const string File = "file";

    /// <summary>The <c>folder</c> kind.</summary>
    public const string Folder = "folder";

    /// <summary>The <c>selection</c> kind.</summary>
    public const string Selection = "selection";

    /// <summary>The <c>errors</c> kind.</summary>
    public const string Errors = "errors";

    /// <summary>The <c>clipboard</c> kind.</summary>
    public const string Clipboard = "clipboard";

    /// <summary>The <c>symbol</c> kind: a definition and a summary of its references.</summary>
    public const string Symbol = "symbol";

    /// <summary>The <c>docs</c> kind: Markdown and text documentation.</summary>
    public const string Docs = "docs";

    /// <summary>The <c>solution</c> kind: the repo map.</summary>
    public const string Solution = "solution";

    /// <summary>The <c>git</c> kind: the working-tree diff, the staged changes or the last commits.</summary>
    public const string Git = "git";

    /// <summary>The <c>output</c> kind: a pane of the Output window.</summary>
    public const string Output = "output";

    /// <summary>The <c>build</c> kind: the last build's output.</summary>
    public const string Build = "build";

    /// <summary>The <c>tests</c> kind: the last test results.</summary>
    public const string Tests = "tests";

    /// <summary>The characters that separate an option from its count (<c>log:5</c>, <c>log 5</c>).</summary>
    internal static readonly char[] OptionSeparators = [':', ' ', ','];

    /// <summary>Gets the kinds, in the order the picker lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [File, Folder, Symbol, Docs, Selection, Errors, Clipboard, Solution, Git, Output, Build, Tests];

    /// <summary>Determines whether a kind takes a value (<c>file</c>, <c>folder</c>, <c>symbol</c> and <c>docs</c>), so a bare word is a fuzzy name.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for the kinds that name something.</returns>
    public static bool TakesValue(string kind) => kind is File or Folder or Symbol or Docs;

    /// <summary>Determines whether a kind takes an optional value from a short list (<c>git</c> and <c>output</c>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for <c>git</c> and <c>output</c>.</returns>
    public static bool HasOptions(string kind) => kind is Git or Output;

    /// <summary>Determines whether picking a kind in the picker types <c>@kind:</c> and lists its values (rather than pinning it at once).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for the kinds that take a value or an option.</returns>
    public static bool OpensSubPicker(string kind) => TakesValue(kind) || HasOptions(kind);

    /// <summary>Determines whether a conversation holds at most one pin of a kind (its content has no name or option).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for <c>selection</c>, <c>errors</c>, <c>clipboard</c>, <c>solution</c>, <c>build</c> and <c>tests</c>.</returns>
    public static bool IsSingleton(string kind) => kind is Selection or Errors or Clipboard or Solution or Build or Tests;

    /// <summary>The arguments a mention sends (<see cref="Contracts.MentionRequest.Arguments"/>): the option of <c>git</c> and <c>output</c>.</summary>
    /// <param name="parsed">The parsed mention.</param>
    /// <returns><c>["log", "5"]</c> for <c>@git:log:5</c>, <c>["debug"]</c> for <c>@output:debug</c>; empty otherwise.</returns>
    public static string[] ArgumentsOf(ParsedMention parsed)
    {
        if (parsed is null || !HasOptions(parsed.Kind) || parsed.Value.Length == 0)
        {
            return [];
        }

        return parsed.Kind == Output
            ? [parsed.Value]
            : parsed.Value.Split(OptionSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Reads a mention with the grammar.</summary>
    /// <param name="mention">The mention, with or without its leading <c>@</c>.</param>
    /// <returns>The kind and value. A bare word that is a value-less kind (<c>@errors</c>) is that kind.</returns>
    public static ParsedMention Parse(string? mention)
    {
        var text = (mention ?? string.Empty).Trim();
        if (text.StartsWith("@", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var kind = text.Substring(0, colon).ToLowerInvariant();
            if (IsKind(kind))
            {
                return new ParsedMention(kind, text.Substring(colon + 1).Trim(), hasKindSeparator: true);
            }
        }

        var lower = text.ToLowerInvariant();
        if (IsKind(lower) && !TakesValue(lower))
        {
            return new ParsedMention(lower, string.Empty, hasKindSeparator: false);
        }

        return new ParsedMention(string.Empty, text, hasKindSeparator: false);
    }

    /// <summary>Determines whether a word is one of <see cref="Kinds"/>.</summary>
    /// <param name="word">The word (lower case).</param>
    /// <returns><see langword="true"/> for a kind.</returns>
    public static bool IsKind(string word) => word is File or Folder or Selection or Errors or Clipboard or Symbol or Docs or Solution or Git or Output or Build or Tests;

    /// <summary>
    /// Finds the mention being typed at the caret: an <c>@</c> at the start of the input or after white space or an
    /// opening bracket or quote, with no white space between it and the caret.
    /// </summary>
    /// <param name="text">The input text.</param>
    /// <param name="caret">The caret index.</param>
    /// <returns>The token, or <see langword="null"/> when the caret is not in a mention.</returns>
    public static MentionToken? FindAt(string? text, int caret)
    {
        text ??= string.Empty;
        if (caret < 1 || caret > text.Length)
        {
            return null;
        }

        var at = -1;
        for (var i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == '@')
            {
                at = i;
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                return null;
            }
        }

        if (at < 0 || (at > 0 && !IsBoundary(text[at - 1])))
        {
            return null; // An e-mail address or a decorator such as foo@bar.
        }

        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return new MentionToken(at, end - at, text.Substring(at + 1, caret - at - 1));
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is '(' or '[' or '{' or '"' or '\'' or '`';
}
