using Pillion.ContextEngine.Chunking.Core;
using TsNode = TreeSitter.Node;
using TsTreeCursor = TreeSitter.TreeCursor;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// HTML by the tree-sitter HTML grammar. An element that fits the cap is one unit; an element that does not becomes a
/// header (its start tag) and its children are walked. Sibling elements and text that fit are grouped, so a long list
/// of small elements becomes a few chunks rather than hundreds. Breadcrumbs name elements by tag, id and first class:
/// <c>index.html &gt; html &gt; body &gt; div#main</c>.
/// </summary>
public sealed class HtmlChunker : SyntaxChunker
{

    /// <inheritdoc />
    public override string Language => LanguageIds.Html;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, ResourceScope scope, out string? failure) =>
        CollectMarkup(Loader, budget, budget.Document.Text, TreeSitterGrammars.Html.MaxErrorFraction, scope, out failure);

    /// <summary>
    /// Finds markup units in <paramref name="text"/>, which has the document's length and line breaks (Razor passes
    /// its markup with the code blocks blanked out).
    /// </summary>
    internal static List<ChunkUnit>? CollectMarkup(GrammarLoader loader, TokenBudget budget, string text,
        double maxErrorFraction, ResourceScope scope, out string? failure)
    {
        var tree = TreeSitterChunker.Parse(loader, text, maxErrorFraction, out failure);
        if (tree is null)
        {
            return null;
        }

        scope.Add(tree);

        var units = new List<ChunkUnit>();
        new MarkupCollector(budget, units).VisitChildren(tree.RootNode, []);
        return units;
    }

    /// <summary>The markup grammar loader, shared with the Razor chunker.</summary>
    internal static GrammarLoader Loader => GrammarLoader.For(TreeSitterGrammars.Html.GrammarId);

    private sealed class MarkupCollector(TokenBudget budget, List<ChunkUnit> units)
    {
        private readonly SourceDocument _document = budget.Document;

        public void VisitChildren(TsNode parent, IReadOnlyList<string> segments)
        {
            using var cursor = new TsTreeCursor(parent);
            if (!cursor.GotoFirstChild())
            {
                return;
            }

            do
            {
                var node = cursor.CurrentNode;
                if (!node.IsNamed || node.IsMissing)
                {
                    continue;
                }

                switch (node.Type)
                {
                    case "element" or "script_element" or "style_element":
                        Element(node, segments);
                        break;
                    case "start_tag" or "end_tag" or "comment":
                        break;
                    case "doctype":
                        Add(node, ChunkKind.Document, segments, null, "document|");
                        break;
                    default:
                        Add(node, ChunkKind.Markup, segments, null, "markup|" + string.Join("|", segments));
                        break;
                }
            }
            while (cursor.GotoNextSibling());
        }

        private void Element(TsNode element, IReadOnlyList<string> segments)
        {
            var label = Label(element);
            var start = Line(element.StartIndex);
            var end = _document.LastLineOfSpan(element.StartIndex, element.EndIndex);
            var fits = budget.Overhead + budget.RangeTokens(start, end) <= budget.MaxTokens * 0.85;
            if (fits || element.Type != "element" || !HasChildElement(element))
            {
                var unit = Add(element, ChunkKind.Markup, segments, label, "markup|" + string.Join("|", segments));
                unit.AddSplitSource(() => ChildStarts(element));
                return;
            }

            IReadOnlyList<string> inner = [.. segments, label];
            var startTag = FirstChild(element, "start_tag") ?? element;
            units.Add(new ChunkUnit(Line(startTag.StartIndex), _document.LastLineOfSpan(startTag.StartIndex, startTag.EndIndex),
                ChunkKind.Markup, inner));
            VisitChildren(element, inner);
        }

        private ChunkUnit Add(TsNode node, ChunkKind kind, IReadOnlyList<string> segments, string? name, string mergeKey)
        {
            var unit = new ChunkUnit(Line(node.StartIndex), _document.LastLineOfSpan(node.StartIndex, node.EndIndex), kind,
                segments, name)
            {
                MergeKey = mergeKey,
            };
            units.Add(unit);
            return unit;
        }

        /// <summary><c>tag#id</c>, or <c>tag.class</c> with the first class, or the tag alone.</summary>
        private string Label(TsNode element)
        {
            var tag = FirstChild(element, "start_tag") ?? FirstChild(element, "self_closing_tag");
            if (tag is null)
            {
                return element.Type.Replace("_element", string.Empty, StringComparison.Ordinal);
            }

            var name = FirstChild(tag, "tag_name") is { } tagName ? Text(tagName) : "element";
            string? id = null;
            string? cssClass = null;
            using var cursor = new TsTreeCursor(tag);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    var attribute = cursor.CurrentNode;
                    if (attribute.Type != "attribute" || FirstChild(attribute, "attribute_name") is not { } attributeName)
                    {
                        continue;
                    }

                    var value = AttributeValue(attribute);
                    switch (Text(attributeName).ToLowerInvariant())
                    {
                        case "id" when value.Length > 0:
                            id = value;
                            break;
                        case "class" when value.Length > 0:
                            cssClass = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            break;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return Breadcrumbs.Sanitize(id is not null ? $"{name}#{id}" : cssClass is not null ? $"{name}.{cssClass}" : name, 60);
        }

        private string AttributeValue(TsNode attribute)
        {
            var value = FirstChild(attribute, "quoted_attribute_value") is { } quoted
                ? FirstChild(quoted, "attribute_value")
                : FirstChild(attribute, "attribute_value");
            return value is null ? string.Empty : Text(value);
        }

        private static bool HasChildElement(TsNode element)
        {
            using var cursor = new TsTreeCursor(element);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    if (cursor.CurrentNode.Type is "element" or "script_element" or "style_element")
                    {
                        return true;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return false;
        }

        private List<int> ChildStarts(TsNode node)
        {
            var lines = new List<int>();
            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    if (cursor.CurrentNode.IsNamed)
                    {
                        lines.Add(Line(cursor.CurrentNode.StartIndex));
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return lines;
        }

        private static TsNode? FirstChild(TsNode node, string type)
        {
            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    if (cursor.CurrentNode.Type == type)
                    {
                        return cursor.CurrentNode;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return null;
        }

        private string Text(TsNode node)
        {
            var start = Math.Clamp(node.StartIndex, 0, _document.Text.Length);
            var end = Math.Clamp(node.EndIndex, start, Math.Min(_document.Text.Length, start + 120));
            return _document.Text[start..end];
        }

        private int Line(int offset) => _document.LineOfOffset(offset);
    }
}
