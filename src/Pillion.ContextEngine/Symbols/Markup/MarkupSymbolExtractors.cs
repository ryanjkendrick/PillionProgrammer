using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.TreeSitter;
using TsNode = TreeSitter.Node;
using TsTreeCursor = TreeSitter.TreeCursor;

namespace Pillion.ContextEngine.Symbols.Markup;

/// <summary>
/// CSS by the tree-sitter CSS grammar, kept small: each class selector of a rule defines a <see cref="SymbolKind.Selector"/>
/// named with its dot (<c>.card</c>) spanning the rule, and each id selector is a use of the HTML id it styles
/// (<c>#top</c>, defined by <see cref="HtmlSymbolExtractor"/>).
/// </summary>
public sealed class CssSymbolExtractor : SyntaxSymbolExtractor
{
    private static GrammarLoader Loader => GrammarLoader.For(TreeSitterGrammars.Css.GrammarId);

    /// <inheritdoc />
    public override string Language => LanguageIds.Css;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        var text = sink.Document.Text;
        using var tree = TreeSitterChunker.Parse(Loader, text, TreeSitterGrammars.Css.MaxErrorFraction, out failure);
        if (tree is null)
        {
            return false;
        }

        var rules = new Stack<(int Depth, TsNode Rule, string Selectors)>();
        using var cursor = new TsTreeCursor(tree.RootNode);
        var depth = 0;
        while (true)
        {
            var node = cursor.CurrentNode;
            switch (node.Type)
            {
                case "rule_set":
                    var selectors = node.NamedChildren.FirstOrDefault(c => c.Type == "selectors") is { } list
                        ? SignatureText.Normalize(MarkupText.Slice(text, list.StartIndex, list.EndIndex))
                        : string.Empty;
                    rules.Push((depth, node, selectors));
                    break;
                case "class_selector" when FirstChild(node, "class_name") is { } className:
                    var rule = rules.Count > 0 ? rules.Peek() : (Depth: depth, Rule: node, Selectors: string.Empty);
                    sink.Define("." + MarkupText.Slice(text, className.StartIndex, className.EndIndex), SymbolKind.Selector, string.Empty,
                        rule.Rule.StartIndex, rule.Rule.EndIndex, rule.Selectors);
                    break;
                case "id_selector" when FirstChild(node, "id_name") is { } idName:
                    sink.Reference("#" + MarkupText.Slice(text, idName.StartIndex, idName.EndIndex), SymbolReferenceKind.Use,
                        idName.StartIndex, null, rules.Count > 0 ? rules.Peek().Selectors : string.Empty);
                    break;
            }

            if (cursor.GotoFirstChild())
            {
                depth++;
                continue;
            }

            while (!cursor.GotoNextSibling())
            {
                if (!cursor.GotoParent())
                {
                    return true;
                }

                depth--;
            }

            while (rules.Count > 0 && rules.Peek().Depth >= depth)
            {
                rules.Pop();
            }
        }
    }

    private static TsNode? FirstChild(TsNode node, string type) => node.NamedChildren.FirstOrDefault(c => c.Type == type);
}

/// <summary>
/// HTML by the tree-sitter HTML grammar, kept small: an <c>id</c> attribute defines an <see cref="SymbolKind.ElementId"/>
/// named with its hash (<c>#top</c>) spanning the element, and each name in a <c>class</c> attribute is a use of the
/// CSS class (<c>.card</c>, defined by <see cref="CssSymbolExtractor"/>).
/// </summary>
public sealed class HtmlSymbolExtractor : SyntaxSymbolExtractor
{
    /// <inheritdoc />
    public override string Language => LanguageIds.Html;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        var text = sink.Document.Text;
        using var tree = TreeSitterChunker.Parse(HtmlChunker.Loader, text, TreeSitterGrammars.Html.MaxErrorFraction, out failure);
        if (tree is null)
        {
            return false;
        }

        using var cursor = new TsTreeCursor(tree.RootNode);
        while (true)
        {
            var node = cursor.CurrentNode;
            if (node.Type is "start_tag" or "self_closing_tag")
            {
                Tag(sink, text, node);
            }

            if (node.Type is not ("start_tag" or "self_closing_tag") && cursor.GotoFirstChild())
            {
                continue;
            }

            while (!cursor.GotoNextSibling())
            {
                if (!cursor.GotoParent())
                {
                    return true;
                }
            }
        }
    }

    private static void Tag(SymbolSink sink, string text, TsNode tag)
    {
        var element = tag.Type == "start_tag" && tag.Parent is { } parent ? parent : tag;
        foreach (var attribute in tag.NamedChildren)
        {
            if (attribute.Type != "attribute" || attribute.NamedChildren.FirstOrDefault(c => c.Type == "attribute_name") is not { } nameNode)
            {
                continue;
            }

            var valueNode = attribute.NamedChildren.FirstOrDefault(c => c.Type == "quoted_attribute_value") is { } quoted
                ? quoted.NamedChildren.FirstOrDefault(c => c.Type == "attribute_value")
                : attribute.NamedChildren.FirstOrDefault(c => c.Type == "attribute_value");
            if (valueNode is null)
            {
                continue;
            }

            var value = MarkupText.Slice(text, valueNode.StartIndex, valueNode.EndIndex);
            switch (MarkupText.Slice(text, nameNode.StartIndex, nameNode.EndIndex).ToLowerInvariant())
            {
                case "id" when IsToken(value):
                    sink.Define("#" + value, SymbolKind.ElementId, string.Empty, element.StartIndex, element.EndIndex,
                        SignatureText.Normalize(MarkupText.Slice(text, tag.StartIndex, tag.EndIndex)));
                    break;
                case "class":
                    var offset = valueNode.StartIndex;
                    foreach (var name in value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (IsToken(name))
                        {
                            sink.Reference("." + name, SymbolReferenceKind.Use, offset, null, string.Empty);
                        }
                    }

                    break;
            }
        }
    }

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 100 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Slices node text out of the document safely.</summary>
internal static class MarkupText
{
    public static string Slice(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, Math.Min(text.Length, start + 400));
        return text[start..end];
    }
}
