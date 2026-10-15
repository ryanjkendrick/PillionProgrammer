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
        // Follow C and C++ declarators down to the name: pointer, reference, function and array declarators wrap it,
        // usually in a "declarator" field, but a reference declarator has its inner declarator as a plain child.
        for (var guard = 0; guard < 32; guard++)
        {
            var inner = node.GetChildForField("declarator");
            if (inner is null && node.Type.EndsWith("_declarator", StringComparison.Ordinal))
            {
                inner = FirstNamedChild(node);
            }

            if (inner is null)
            {
                break;
            }

            node = inner;
        }

        return Slice(document, node.StartIndex, node.EndIndex);
    }

    /// <summary>The text of a node's first named child (a package clause's name).</summary>
    public static string FirstNamedChildText(SourceDocument document, TsNode node) =>
        FirstNamedChild(node) is { } child ? Slice(document, child.StartIndex, child.EndIndex) : string.Empty;

    private static TsNode? FirstNamedChild(TsNode node)
    {
        using var cursor = new TsTreeCursor(node);
        if (cursor.GotoFirstChild())
        {
            do
            {
                var child = cursor.CurrentNode;
                if (child.IsNamed)
                {
                    return child;
                }
            }
            while (cursor.GotoNextSibling());
        }

        return null;
    }

    /// <summary>
    /// A node's text up to its opening brace (or its first line when there is no brace nearby), for nodes without a
    /// name: CSS selectors over several lines, <c>@media</c> queries.
    /// </summary>
    public static string HeaderText(SourceDocument document, TsNode node)
    {
        var start = Math.Clamp(node.StartIndex, 0, document.Text.Length);
        var end = Math.Clamp(node.EndIndex, start, Math.Min(document.Text.Length, start + MaxNameSource));
        var span = document.Text.AsSpan(start, end - start);
        var stop = span.IndexOf('{');
        if (stop < 0)
        {
            stop = span.IndexOfAny('\n', '\r');
        }

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
