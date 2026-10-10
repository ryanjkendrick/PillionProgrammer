using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pillion.VisualStudio.Core.Markdown;

/// <summary>
/// A chat message parsed into Markdown blocks (design doc section 5). Has no WPF dependency: the VSIX maps each block to
/// WPF elements and re-renders only the blocks that changed (see <see cref="MarkdownRenderPlan"/>).
/// </summary>
public sealed class MarkdownMessage
{
    /// <summary>An empty message.</summary>
    public static readonly MarkdownMessage Empty = new(string.Empty, [], string.Empty);

    /// <summary>Initializes a new instance of the <see cref="MarkdownMessage"/> class.</summary>
    /// <param name="text">The Markdown text.</param>
    /// <param name="blocks">The top-level blocks, in order.</param>
    /// <param name="definitionsKey">A key for the message's link reference definitions (empty when it has none).</param>
    public MarkdownMessage(string text, IReadOnlyList<MarkdownBlock> blocks, string definitionsKey)
    {
        Text = text ?? string.Empty;
        Blocks = blocks ?? [];
        DefinitionsKey = definitionsKey ?? string.Empty;
    }

    /// <summary>Gets the Markdown text the blocks were parsed from.</summary>
    public string Text { get; }

    /// <summary>Gets the top-level blocks, in order.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; }

    /// <summary>
    /// Gets a key for the link reference definitions (<c>[label]: url</c>). A definition can change how an earlier block
    /// renders, so when this differs between two parses every block is re-rendered.
    /// </summary>
    public string DefinitionsKey { get; }
}

/// <summary>The kind of a <see cref="MarkdownBlock"/>.</summary>
public enum MarkdownBlockKind
{
    /// <summary>A paragraph (<see cref="MarkdownParagraph"/>).</summary>
    Paragraph,

    /// <summary>A heading (<see cref="MarkdownHeading"/>).</summary>
    Heading,

    /// <summary>A bulleted or numbered list (<see cref="MarkdownList"/>).</summary>
    List,

    /// <summary>One item of a list (<see cref="MarkdownListItem"/>).</summary>
    ListItem,

    /// <summary>A block quote (<see cref="MarkdownQuote"/>).</summary>
    Quote,

    /// <summary>A fenced or indented code block (<see cref="MarkdownCodeBlock"/>).</summary>
    Code,

    /// <summary>A pipe table (<see cref="MarkdownTable"/>).</summary>
    Table,

    /// <summary>A thematic break (<see cref="MarkdownRule"/>).</summary>
    Rule,
}

/// <summary>One block of a parsed message. Blocks are immutable.</summary>
public abstract class MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownBlock"/> class.</summary>
    /// <param name="source">The Markdown source of the block.</param>
    protected MarkdownBlock(string source)
    {
        Source = source ?? string.Empty;
    }

    /// <summary>Gets the kind of block.</summary>
    public abstract MarkdownBlockKind Kind { get; }

    /// <summary>Gets the block's Markdown source. Two blocks of the same kind with the same source render the same.</summary>
    public string Source { get; }

    /// <summary>Gets the child blocks of a container (list, list item, quote); empty for other blocks.</summary>
    public virtual IReadOnlyList<MarkdownBlock> Children => [];

    /// <summary>
    /// Gets a value indicating whether this block renders the same as <paramref name="other"/> (same kind and source).
    /// </summary>
    /// <param name="other">The other block.</param>
    /// <returns><see langword="true"/> when the rendered element can be kept as it is.</returns>
    public bool RendersSameAs(MarkdownBlock? other) =>
        other is not null && other.Kind == Kind && string.Equals(other.Source, Source, StringComparison.Ordinal) && ContentEquals(other);

    /// <summary>
    /// Gets a value indicating whether an element rendered for this block can be updated in place to show
    /// <paramref name="replacement"/> (for example a paragraph whose text grew, a code block whose code grew, or a list that gained
    /// an item) instead of being replaced.
    /// </summary>
    /// <param name="replacement">The block that replaces this one.</param>
    /// <returns><see langword="true"/> when the element can be updated in place.</returns>
    public abstract bool CanUpdateTo(MarkdownBlock replacement);

    /// <inheritdoc/>
    public override string ToString() => Kind + ": " + Source;

    /// <summary>Compares content that the source alone may not show (called only for blocks of the same kind and source).</summary>
    /// <param name="other">A block of the same kind with the same source.</param>
    /// <returns><see langword="true"/> when the two render the same.</returns>
    protected virtual bool ContentEquals(MarkdownBlock other) => true;
}

/// <summary>A paragraph of inline content.</summary>
public sealed class MarkdownParagraph : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownParagraph"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="inlines">The inline content.</param>
    public MarkdownParagraph(string source, IReadOnlyList<MarkdownInline> inlines)
        : base(source)
    {
        Inlines = inlines ?? [];
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Paragraph;

    /// <summary>Gets the inline content.</summary>
    public IReadOnlyList<MarkdownInline> Inlines { get; }

    /// <summary>Gets the paragraph as plain text (for screen readers).</summary>
    public string PlainText => MarkdownInline.ToPlainText(Inlines);

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) => replacement is MarkdownParagraph;
}

/// <summary>A heading, level 1 to 6.</summary>
public sealed class MarkdownHeading : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownHeading"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="level">The level, 1 to 6.</param>
    /// <param name="inlines">The inline content.</param>
    public MarkdownHeading(string source, int level, IReadOnlyList<MarkdownInline> inlines)
        : base(source)
    {
        Level = Math.Max(1, Math.Min(6, level));
        Inlines = inlines ?? [];
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Heading;

    /// <summary>Gets the level, 1 (largest) to 6.</summary>
    public int Level { get; }

    /// <summary>Gets the inline content.</summary>
    public IReadOnlyList<MarkdownInline> Inlines { get; }

    /// <summary>Gets the heading as plain text.</summary>
    public string PlainText => MarkdownInline.ToPlainText(Inlines);

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) => replacement is MarkdownHeading heading && heading.Level == Level;
}

/// <summary>A bulleted or numbered list; its children are <see cref="MarkdownListItem"/> blocks.</summary>
public sealed class MarkdownList : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownList"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="isOrdered">Whether the list is numbered.</param>
    /// <param name="start">The first number of a numbered list.</param>
    /// <param name="isLoose">Whether the items are separated by blank lines.</param>
    /// <param name="items">The items.</param>
    public MarkdownList(string source, bool isOrdered, int start, bool isLoose, IReadOnlyList<MarkdownListItem> items)
        : base(source)
    {
        IsOrdered = isOrdered;
        Start = start;
        IsLoose = isLoose;
        Items = items ?? [];
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.List;

    /// <summary>Gets a value indicating whether the list is numbered.</summary>
    public bool IsOrdered { get; }

    /// <summary>Gets the first number of a numbered list (1 for a bulleted list).</summary>
    public int Start { get; }

    /// <summary>Gets a value indicating whether the items are separated by blank lines (loose) or not (tight).</summary>
    public bool IsLoose { get; }

    /// <summary>Gets the items.</summary>
    public IReadOnlyList<MarkdownListItem> Items { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<MarkdownBlock> Children => Items;

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) =>
        replacement is MarkdownList list && list.IsOrdered == IsOrdered && list.Start == Start && list.IsLoose == IsLoose;
}

/// <summary>One item of a <see cref="MarkdownList"/>: a marker and child blocks.</summary>
public sealed class MarkdownListItem : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownListItem"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="marker">The marker shown before the item ("•" or "3.").</param>
    /// <param name="blocks">The item's blocks.</param>
    public MarkdownListItem(string source, string marker, IReadOnlyList<MarkdownBlock> blocks)
        : base(source)
    {
        Marker = marker ?? string.Empty;
        Blocks = blocks ?? [];
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.ListItem;

    /// <summary>Gets the marker shown before the item.</summary>
    public string Marker { get; }

    /// <summary>Gets the item's blocks.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<MarkdownBlock> Children => Blocks;

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) =>
        replacement is MarkdownListItem item && string.Equals(item.Marker, Marker, StringComparison.Ordinal);
}

/// <summary>A block quote; its children are blocks.</summary>
public sealed class MarkdownQuote : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownQuote"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="blocks">The quoted blocks.</param>
    public MarkdownQuote(string source, IReadOnlyList<MarkdownBlock> blocks)
        : base(source)
    {
        Blocks = blocks ?? [];
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Quote;

    /// <summary>Gets the quoted blocks.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<MarkdownBlock> Children => Blocks;

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) => replacement is MarkdownQuote;
}

/// <summary>A fenced (or indented) code block, shown in an embedded editor view.</summary>
public sealed class MarkdownCodeBlock : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownCodeBlock"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="code">The code, without the fence lines, with <c>\n</c> line breaks.</param>
    /// <param name="info">The fence's info string up to the first space (for example <c>csharp</c>), or empty.</param>
    /// <param name="isClosed">Whether the closing fence has arrived (an open fence is still streaming).</param>
    public MarkdownCodeBlock(string source, string code, string info, bool isClosed)
        : base(source)
    {
        Code = code ?? string.Empty;
        Info = info ?? string.Empty;
        IsClosed = isClosed;
        Language = CodeLanguage.FromFenceInfo(Info);
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Code;

    /// <summary>Gets the code, with <c>\n</c> line breaks and no trailing line break.</summary>
    public string Code { get; }

    /// <summary>Gets the fence's language tag as written, or empty.</summary>
    public string Info { get; }

    /// <summary>Gets the language the tag maps to (plain text when it is unknown or missing).</summary>
    public CodeLanguage Language { get; }

    /// <summary>Gets a value indicating whether the closing fence has arrived.</summary>
    public bool IsClosed { get; }

    /// <inheritdoc/>
    /// <remarks>The editor view keeps its content type, so the language must not change.</remarks>
    public override bool CanUpdateTo(MarkdownBlock replacement) =>
        replacement is MarkdownCodeBlock code && ReferenceEquals(code.Language, Language);

    /// <inheritdoc/>
    /// <remarks>Trailing blank lines inside a fence that is still open are code, but not part of the trimmed source.</remarks>
    protected override bool ContentEquals(MarkdownBlock other) =>
        other is MarkdownCodeBlock code && code.IsClosed == IsClosed && string.Equals(code.Code, Code, StringComparison.Ordinal);
}

/// <summary>The alignment of a <see cref="MarkdownTable"/> column.</summary>
public enum MarkdownColumnAlignment
{
    /// <summary>Not specified (left).</summary>
    None,

    /// <summary>Left aligned.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right aligned.</summary>
    Right,
}

/// <summary>A pipe table: a header row, body rows and column alignments. Cells hold inline content.</summary>
public sealed class MarkdownTable : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownTable"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    /// <param name="rows">The rows; each row is a list of cells and each cell a list of inlines.</param>
    /// <param name="headerRows">How many of the first rows are header rows.</param>
    /// <param name="alignments">The column alignments.</param>
    public MarkdownTable(
        string source,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> rows,
        int headerRows,
        IReadOnlyList<MarkdownColumnAlignment> alignments)
        : base(source)
    {
        Rows = rows ?? [];
        HeaderRows = headerRows;
        Alignments = alignments ?? [];
        ColumnCount = Math.Max(Alignments.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Count));
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Table;

    /// <summary>Gets the rows (header rows first): each row is a list of cells, each cell a list of inlines.</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows { get; }

    /// <summary>Gets how many of the first rows are header rows.</summary>
    public int HeaderRows { get; }

    /// <summary>Gets the column alignments.</summary>
    public IReadOnlyList<MarkdownColumnAlignment> Alignments { get; }

    /// <summary>Gets the number of columns.</summary>
    public int ColumnCount { get; }

    /// <summary>Gets the alignment of a column.</summary>
    /// <param name="column">The column index.</param>
    /// <returns>The alignment.</returns>
    public MarkdownColumnAlignment AlignmentOf(int column) =>
        column >= 0 && column < Alignments.Count ? Alignments[column] : MarkdownColumnAlignment.None;

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) => false;
}

/// <summary>A thematic break (a horizontal rule).</summary>
public sealed class MarkdownRule : MarkdownBlock
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownRule"/> class.</summary>
    /// <param name="source">The Markdown source.</param>
    public MarkdownRule(string source)
        : base(source)
    {
    }

    /// <inheritdoc/>
    public override MarkdownBlockKind Kind => MarkdownBlockKind.Rule;

    /// <inheritdoc/>
    public override bool CanUpdateTo(MarkdownBlock replacement) => replacement is MarkdownRule;
}

/// <summary>The kind of a <see cref="MarkdownInline"/>.</summary>
public enum MarkdownInlineKind
{
    /// <summary>Literal text.</summary>
    Text,

    /// <summary>Inline code (<c>`code`</c>).</summary>
    Code,

    /// <summary>Emphasis (italic); has children.</summary>
    Emphasis,

    /// <summary>Strong emphasis (bold); has children.</summary>
    Strong,

    /// <summary>Strikethrough (<c>~~text~~</c>); has children.</summary>
    Strikethrough,

    /// <summary>A link; has children (the link text) and a URL.</summary>
    Link,

    /// <summary>An image. Never fetched: only its alt text is shown.</summary>
    Image,

    /// <summary>A line break (soft or hard).</summary>
    LineBreak,
}

/// <summary>Inline content of a paragraph, heading or table cell. Inlines are immutable.</summary>
public sealed class MarkdownInline
{
    private MarkdownInline(MarkdownInlineKind kind, string text, IReadOnlyList<MarkdownInline> children, string? url, Uri? safeUri, bool isHardBreak)
    {
        Kind = kind;
        Text = text;
        Children = children;
        Url = url;
        SafeUri = safeUri;
        IsHardBreak = isHardBreak;
    }

    /// <summary>Gets the kind of inline.</summary>
    public MarkdownInlineKind Kind { get; }

    /// <summary>Gets the text of a text or code inline, or an image's alt text; empty for the others.</summary>
    public string Text { get; }

    /// <summary>Gets the children of emphasis, strong, strikethrough and link inlines.</summary>
    public IReadOnlyList<MarkdownInline> Children { get; }

    /// <summary>Gets the URL of a link or image as written (images are never fetched).</summary>
    public string? Url { get; }

    /// <summary>
    /// Gets the link's URL when it is safe to open in a browser (see <see cref="LinkSafety"/>); <see langword="null"/> for
    /// links that must not be opened, such as <c>javascript:</c>, <c>file:</c> or relative URLs.
    /// </summary>
    public Uri? SafeUri { get; }

    /// <summary>Gets a value indicating whether a line break is a hard break (otherwise it is shown as a space).</summary>
    public bool IsHardBreak { get; }

    /// <summary>Creates a text inline.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateText(string text) => new(MarkdownInlineKind.Text, text ?? string.Empty, [], null, null, false);

    /// <summary>Creates an inline code span.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateCode(string code) => new(MarkdownInlineKind.Code, code ?? string.Empty, [], null, null, false);

    /// <summary>Creates an emphasis, strong or strikethrough inline.</summary>
    /// <param name="kind"><see cref="MarkdownInlineKind.Emphasis"/>, <see cref="MarkdownInlineKind.Strong"/> or <see cref="MarkdownInlineKind.Strikethrough"/>.</param>
    /// <param name="children">The emphasised content.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateSpan(MarkdownInlineKind kind, IReadOnlyList<MarkdownInline> children)
    {
        if (kind is not (MarkdownInlineKind.Emphasis or MarkdownInlineKind.Strong or MarkdownInlineKind.Strikethrough))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only emphasis, strong and strikethrough are spans.");
        }

        return new(kind, string.Empty, children ?? [], null, null, false);
    }

    /// <summary>Creates a link inline; <see cref="SafeUri"/> is set only when <see cref="LinkSafety"/> allows the URL.</summary>
    /// <param name="url">The URL as written.</param>
    /// <param name="children">The link text.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateLink(string? url, IReadOnlyList<MarkdownInline> children) =>
        new(MarkdownInlineKind.Link, string.Empty, children ?? [], url, LinkSafety.TryGetSafeUri(url, out var safe) ? safe : null, false);

    /// <summary>Creates an image inline. The image is never fetched; the alt text is shown instead.</summary>
    /// <param name="altText">The alt text.</param>
    /// <param name="url">The image URL as written.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateImage(string altText, string? url) =>
        new(MarkdownInlineKind.Image, altText ?? string.Empty, [], url, null, false);

    /// <summary>Creates a line break.</summary>
    /// <param name="isHard">Whether it is a hard break.</param>
    /// <returns>The inline.</returns>
    public static MarkdownInline CreateLineBreak(bool isHard) => new(MarkdownInlineKind.LineBreak, string.Empty, [], null, null, isHard);

    /// <summary>Flattens inlines to plain text (link text, code text and image alt text included).</summary>
    /// <param name="inlines">The inlines.</param>
    /// <returns>The text.</returns>
    public static string ToPlainText(IEnumerable<MarkdownInline> inlines)
    {
        var builder = new StringBuilder();
        Append(builder, inlines);
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        MarkdownInlineKind.Text or MarkdownInlineKind.Code or MarkdownInlineKind.Image => Kind + "(" + Text + ")",
        MarkdownInlineKind.LineBreak => IsHardBreak ? "HardBreak" : "SoftBreak",
        MarkdownInlineKind.Link => "Link(" + Url + ": " + ToPlainText(Children) + ")",
        _ => Kind + "(" + ToPlainText(Children) + ")",
    };

    private static void Append(StringBuilder builder, IEnumerable<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text:
                case MarkdownInlineKind.Code:
                case MarkdownInlineKind.Image:
                    builder.Append(inline.Text);
                    break;
                case MarkdownInlineKind.LineBreak:
                    builder.Append(inline.IsHardBreak ? '\n' : ' ');
                    break;
                default:
                    Append(builder, inline.Children);
                    break;
            }
        }
    }
}
