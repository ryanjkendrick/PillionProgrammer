using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Pillion.VisualStudio.Core.Markdown;

/// <summary>
/// Parses chat replies with Markdig into the <see cref="MarkdownMessage"/> block model: CommonMark plus pipe tables,
/// strikethrough and bare-URL autolinks. Raw HTML is never interpreted (it shows as text), images become their alt text,
/// and links carry a <see cref="MarkdownInline.SafeUri"/> only when <see cref="LinkSafety"/> allows them. Thread-safe.
/// </summary>
/// <remarks>
/// Text that is still streaming parses as the Markdown it is so far: an unclosed fence is a code block to the end of the
/// text, and a table appears once its delimiter row has arrived.
/// </remarks>
public static class ChatMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    /// <summary>Parses a message.</summary>
    /// <param name="text">The Markdown text (any line endings).</param>
    /// <returns>The parsed message.</returns>
    public static MarkdownMessage Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return MarkdownMessage.Empty;
        }

        var source = text!.IndexOf('\r') >= 0 ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
        var document = Markdig.Markdown.Parse(source, Pipeline);
        var definitions = new StringBuilder();
        var blocks = ConvertBlocks(document, source, source.Length, definitions);
        return new MarkdownMessage(source, blocks, definitions.ToString());
    }

    /// <summary>
    /// Converts a container's children. Each child's <see cref="MarkdownBlock.Source"/> runs from its start to the next
    /// sibling's start (or the container's end): Markdig does not extend the span of a block left open at the end of the
    /// text, such as a fence still streaming, so its own span would hide the newest text from the render plan.
    /// </summary>
    private static List<MarkdownBlock> ConvertBlocks(ContainerBlock container, string source, int end, StringBuilder definitions)
    {
        var blocks = new List<MarkdownBlock>(container.Count);
        for (var i = 0; i < container.Count; i++)
        {
            var block = container[i];
            var blockEnd = i + 1 < container.Count ? Math.Max(block.Span.Start, container[i + 1].Span.Start) : end;
            if (Convert(block, source, blockEnd, definitions) is { } converted)
            {
                blocks.Add(converted);
            }
        }

        return blocks;
    }

    private static MarkdownBlock? Convert(Block block, string source, int end, StringBuilder definitions)
    {
        var text = SourceOf(block, source, end);
        switch (block)
        {
            case LinkReferenceDefinitionGroup group:
                foreach (var definition in group.OfType<LinkReferenceDefinition>())
                {
                    definitions.Append(definition.Label).Append('\u0001').Append(definition.Url).Append('\u0002');
                }

                return null;
            case HeadingBlock heading:
                return new MarkdownHeading(text, heading.Level, ConvertInlines(heading.Inline));
            case ParagraphBlock paragraph:
                return new MarkdownParagraph(text, ConvertInlines(paragraph.Inline));
            case FencedCodeBlock fenced:
                return new MarkdownCodeBlock(text, CodeOf(fenced), fenced.Info ?? string.Empty, fenced.ClosingFencedCharCount > 0);
            case CodeBlock code:
                return new MarkdownCodeBlock(text, CodeOf(code), string.Empty, isClosed: true);
            case Table table:
                return ConvertTable(table, text);
            case ListBlock list:
                return ConvertList(list, text, source, end, definitions);
            case QuoteBlock quote:
                return new MarkdownQuote(text, ConvertBlocks(quote, source, end, definitions));
            case ThematicBreakBlock:
                return new MarkdownRule(text);
            case ContainerBlock other:
                return other.Count == 0 ? null : new MarkdownQuote(text, ConvertBlocks(other, source, end, definitions));
            case LeafBlock leaf:
                // Anything else (for example HTML, which is disabled, or a block from an extension) shows as its text.
                var leafText = leaf.Lines.ToString();
                return leafText.Trim().Length == 0 ? null : new MarkdownParagraph(text, [MarkdownInline.CreateText(leafText)]);
            default:
                return null;
        }
    }

    private static MarkdownList ConvertList(ListBlock list, string text, string source, int end, StringBuilder definitions)
    {
        var start = 1;
        if (list.IsOrdered && !int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            start = 1;
        }

        var items = new List<MarkdownListItem>(list.Count);
        var number = start;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not ListItemBlock item)
            {
                continue;
            }

            var itemEnd = i + 1 < list.Count ? Math.Max(item.Span.Start, list[i + 1].Span.Start) : end;
            var marker = list.IsOrdered ? number.ToString(CultureInfo.CurrentCulture) + "." : "•";
            items.Add(new MarkdownListItem(SourceOf(item, source, itemEnd), marker, ConvertBlocks(item, source, itemEnd, definitions)));
            number++;
        }

        return new MarkdownList(text, list.IsOrdered, start, list.IsLoose, items);
    }

    private static MarkdownTable ConvertTable(Table table, string text)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>>();
        var headerRows = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            if (row.IsHeader && rows.Count == headerRows)
            {
                headerRows++;
            }

            var cells = new List<IReadOnlyList<MarkdownInline>>();
            foreach (var cell in row.OfType<TableCell>())
            {
                var inlines = new List<MarkdownInline>();
                foreach (var paragraph in cell.OfType<ParagraphBlock>())
                {
                    if (inlines.Count > 0)
                    {
                        inlines.Add(MarkdownInline.CreateLineBreak(isHard: true));
                    }

                    inlines.AddRange(ConvertInlines(paragraph.Inline));
                }

                cells.Add(inlines);
            }

            rows.Add(cells);
        }

        var alignments = table.ColumnDefinitions
            .Select(c => c.Alignment switch
            {
                TableColumnAlign.Left => MarkdownColumnAlignment.Left,
                TableColumnAlign.Center => MarkdownColumnAlignment.Center,
                TableColumnAlign.Right => MarkdownColumnAlignment.Right,
                _ => MarkdownColumnAlignment.None,
            })
            .ToList();
        return new MarkdownTable(text, rows, headerRows, alignments);
    }

    private static List<MarkdownInline> ConvertInlines(ContainerInline? container)
    {
        var inlines = new List<MarkdownInline>();
        if (container is null)
        {
            return inlines;
        }

        foreach (var inline in container)
        {
            Append(inlines, inline);
        }

        return inlines;
    }

    private static void Append(List<MarkdownInline> inlines, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendText(inlines, literal.Content.ToString());
                break;
            case HtmlEntityInline entity:
                AppendText(inlines, entity.Transcoded.ToString());
                break;
            case CodeInline code:
                inlines.Add(MarkdownInline.CreateCode(code.Content));
                break;
            case LineBreakInline lineBreak:
                inlines.Add(MarkdownInline.CreateLineBreak(lineBreak.IsHard));
                break;
            case AutolinkInline autolink:
                var url = autolink.IsEmail && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    ? "mailto:" + autolink.Url
                    : autolink.Url;
                inlines.Add(MarkdownInline.CreateLink(url, [MarkdownInline.CreateText(autolink.Url)]));
                break;
            case LinkInline { IsImage: true } image:
                inlines.Add(MarkdownInline.CreateImage(MarkdownInline.ToPlainText(ConvertInlines(image)), image.Url));
                break;
            case LinkInline link:
                var children = ConvertInlines(link);
                if (children.Count == 0)
                {
                    children.Add(MarkdownInline.CreateText(link.Url ?? string.Empty));
                }

                inlines.Add(MarkdownInline.CreateLink(link.Url, children));
                break;
            case EmphasisInline emphasis:
                var kind = emphasis.DelimiterChar == '~'
                    ? MarkdownInlineKind.Strikethrough
                    : emphasis.DelimiterCount >= 2 ? MarkdownInlineKind.Strong : MarkdownInlineKind.Emphasis;
                inlines.Add(MarkdownInline.CreateSpan(kind, ConvertInlines(emphasis)));
                break;
            case HtmlInline html:
                AppendText(inlines, html.Tag);
                break;
            case DelimiterInline delimiter:
                // An unmatched delimiter (for example a lone "*" while a reply streams) is literal text.
                AppendText(inlines, delimiter.ToLiteral());
                foreach (var child in delimiter)
                {
                    Append(inlines, child);
                }

                break;
            case ContainerInline other:
                foreach (var child in other)
                {
                    Append(inlines, child);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Appends text, merging it into the previous text inline (literals arrive split at delimiters).</summary>
    private static void AppendText(List<MarkdownInline> inlines, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (inlines.Count > 0 && inlines[inlines.Count - 1].Kind == MarkdownInlineKind.Text)
        {
            inlines[inlines.Count - 1] = MarkdownInline.CreateText(inlines[inlines.Count - 1].Text + text);
        }
        else
        {
            inlines.Add(MarkdownInline.CreateText(text));
        }
    }

    private static string CodeOf(LeafBlock block)
    {
        var lines = block.Lines;
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    private static string SourceOf(Block block, string source, int end)
    {
        var start = Math.Max(0, Math.Min(block.Span.Start, source.Length));
        end = Math.Max(start, Math.Min(Math.Max(end, block.Span.End + 1), source.Length));

        // Blank lines between blocks belong to neither: a block followed by a new one renders as it did at the end.
        while (end > start && source[end - 1] is '\n' or ' ' or '\t')
        {
            end--;
        }

        return source.Substring(start, end - start);
    }
}
