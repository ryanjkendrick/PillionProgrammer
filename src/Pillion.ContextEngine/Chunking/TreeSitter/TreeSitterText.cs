using Pillion.ContextEngine.Chunking.Core;
using TsNode = TreeSitter.Node;
using TsTreeCursor = TreeSitter.TreeCursor;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>Reads names out of tree-sitter nodes without copying more text than a name needs.</summary>
internal static class TreeSitterText
{
    private const int MaxNameSource = 200;

    /// <summary>The text of a name node; a C-style declarator is followed down to its identifier first.</summary>
    public static string NameText(SourceDocument document, TsNode node)
    {
        var guard = 0;
        while (guard++ < 32 && node.GetChildForField("declarator") is { } inner)
        {
            node = inner;
        }

        return Slice(document, node.StartIndex, node.EndIndex);
    }

    /// <summary>The text of a node's first named child (a package clause's name).</summary>
    public static string FirstNamedChildText(SourceDocument document, TsNode node)
    {
        using var cursor = new TsTreeCursor(node);
        if (cursor.GotoFirstChild())
        {
            do
            {
                var child = cursor.CurrentNode;
                if (child.IsNamed)
                {
                    return Slice(document, child.StartIndex, child.EndIndex);
                }
            }
            while (cursor.GotoNextSibling());
        }

        return string.Empty;
    }

    /// <summary>A node's first line up to an opening brace, for nodes without a name (CSS selectors, <c>@media</c>).</summary>
    public static string HeaderText(SourceDocument document, TsNode node)
    {
        var start = Math.Clamp(node.StartIndex, 0, document.Text.Length);
        var end = Math.Clamp(node.EndIndex, start, Math.Min(document.Text.Length, start + MaxNameSource));
        var span = document.Text.AsSpan(start, end - start);
        var stop = span.IndexOfAny('{', '\n', '\r');
        if (stop >= 0)
        {
            span = span[..stop];
        }

        return Breadcrumbs.Sanitize(span.TrimEnd().TrimEnd(':').ToString());
    }

    private static string Slice(SourceDocument document, int start, int end)
    {
        start = Math.Clamp(start, 0, document.Text.Length);
        end = Math.Clamp(end, start, Math.Min(document.Text.Length, start + MaxNameSource));
        return Breadcrumbs.Sanitize(document.Text[start..end]);
    }
}
