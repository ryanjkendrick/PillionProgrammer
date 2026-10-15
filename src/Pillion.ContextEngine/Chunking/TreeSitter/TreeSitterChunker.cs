using System.Globalization;
using Pillion.ContextEngine.Chunking.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsParser = TreeSitter.Parser;
using TsQuery = TreeSitter.Query;
using TsTree = TreeSitter.Tree;
using TsTreeCursor = TreeSitter.TreeCursor;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// A syntactic chunker for any tree-sitter grammar described by a <see cref="TreeSitterGrammar"/>: namespaces and
/// enclosing types in the breadcrumb, a header chunk per type, a chunk per member, file-level code as document chunks,
/// members over the cap split where statements start. A grammar that cannot be loaded, a parse that returns nothing,
/// or a tree with too much of the text inside error nodes falls back to windows.
/// </summary>
public sealed class TreeSitterChunker : SyntaxChunker
{
    private readonly GrammarLoader _loader;

    /// <summary>Creates a chunker for a grammar.</summary>
    /// <param name="grammar">The grammar description.</param>
    public TreeSitterChunker(TreeSitterGrammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        Grammar = grammar;
        _loader = GrammarLoader.For(grammar.GrammarId);
    }

    /// <summary>The grammar description.</summary>
    public TreeSitterGrammar Grammar { get; }

    /// <inheritdoc />
    public override string Language => Grammar.Language;

    /// <summary>True when the grammar's native library loads on this machine.</summary>
    public bool IsAvailable => _loader.Language is not null;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, ResourceScope scope, out string? failure)
    {
        var tree = Parse(_loader, budget.Document.Text, Grammar.MaxErrorFraction, out failure);
        if (tree is null)
        {
            return null;
        }

        scope.Add(tree);

        var units = new List<ChunkUnit>();
        new Collector(Grammar, budget.Document, units).Visit(tree.RootNode, []);
        return units;
    }

    /// <summary>Parses text with a grammar; null with a reason when the grammar is missing or the parse failed badly.</summary>
    internal static TsTree? Parse(GrammarLoader loader, string text, double maxErrorFraction, out string? failure)
    {
        failure = null;
        var language = loader.Language;
        if (language is null)
        {
            failure = loader.Error;
            return null;
        }

        using var parser = new TsParser(language);
        var tree = parser.Parse(text);
        if (tree is null)
        {
            failure = "The parser returned no tree.";
            return null;
        }

        var root = tree.RootNode;
        if (root.HasError && text.Length > 0)
        {
            var fraction = ErrorFraction(language, root) / (double)text.Length;
            if (fraction > maxErrorFraction)
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"{fraction:P0} of the text is inside syntax errors.");
                tree.Dispose();
                return null;
            }
        }

        return tree;
    }

    /// <summary>The number of characters covered by error nodes (nested ones counted once).</summary>
    private static long ErrorFraction(TsLanguage language, TsNode root)
    {
        using var query = new TsQuery(language, "(ERROR) @error");
        var spans = query.Execute(root).Captures
            .Select(c => (Start: c.Node.StartIndex, End: c.Node.EndIndex))
            .OrderBy(s => s.Start)
            .ToList();
        long covered = 0;
        var reach = -1;
        foreach (var (start, end) in spans)
        {
            var from = Math.Max(start, reach);
            if (end > from)
            {
                covered += end - from;
                reach = end;
            }
        }

        return covered;
    }

    /// <summary>Walks a tree and emits units according to the grammar description.</summary>
    private sealed class Collector(TreeSitterGrammar grammar, SourceDocument document, List<ChunkUnit> units)
    {
        public void Visit(TsNode container, IReadOnlyList<string> segments)
        {
            using var cursor = new TsTreeCursor(container);
            if (!cursor.GotoFirstChild())
            {
                return;
            }

            var current = segments;
            do
            {
                var node = cursor.CurrentNode;
                if (!node.IsNamed || node.IsMissing)
                {
                    continue;
                }

                var type = node.Type;
                if (grammar.Leading.Contains(type))
                {
                    continue;
                }

                if (grammar.FileNamespaces.Contains(type))
                {
                    AddDocument(node, current);
                    current = [.. current, TreeSitterText.FirstNamedChildText(document, node)];
                    continue;
                }

                Classify(node, node, current);
            }
            while (cursor.GotoNextSibling());
        }

        private void Classify(TsNode node, TsNode range, IReadOnlyList<string> segments)
        {
            var type = node.Type;
            if (grammar.Wrappers.Contains(type))
            {
                var inner = Inner(node);
                if (inner is not null)
                {
                    Classify(inner, range, segments);
                }
                else
                {
                    AddDocument(range, segments);
                }

                return;
            }

            if (grammar.TransparentContainers.Contains(type))
            {
                Visit(Body(node) ?? node, segments);
                return;
            }

            if (grammar.Namespaces.Contains(type))
            {
                var body = Body(node);
                if (body is null)
                {
                    AddDocument(range, segments);
                }
                else
                {
                    Visit(body, [.. segments, Name(node)]);
                }

                return;
            }

            if (grammar.Types.Contains(type))
            {
                VisitType(node, range, segments);
                return;
            }

            if (grammar.Members.Contains(type) || grammar.FieldMembers.Contains(type))
            {
                AddMember(node, range, segments, Name(node),
                    grammar.FieldMembers.Contains(type) ? "fields|" + string.Join("|", segments) : null);
                return;
            }

            if (grammar.FunctionValueDeclarations.Contains(type) && FunctionValueName(node) is { } functionName)
            {
                AddMember(node, range, segments, functionName, null);
                return;
            }

            AddDocument(range, segments);
        }

        private void VisitType(TsNode node, TsNode range, IReadOnlyList<string> segments)
        {
            IReadOnlyList<string> inner = [.. segments, Name(node)];
            var start = Line(range.StartIndex);
            var end = document.LastLineOfSpan(range.StartIndex, range.EndIndex);
            var body = Body(node);
            if (body is null)
            {
                AddWhole(node, start, end, inner);
                return;
            }

            var mark = units.Count;
            Visit(body, inner);
            var children = units.Count - mark;
            var firstMember = -1;
            for (var i = mark; i < units.Count; i++)
            {
                if (units[i].Kind is ChunkKind.Member or ChunkKind.TypeHeader)
                {
                    firstMember = i;
                    break;
                }
            }

            if (firstMember < 0)
            {
                units.RemoveRange(mark, children);
                AddWhole(body, start, end, inner);
                return;
            }

            // The header is the declaration up to the body, plus any content of the body before the first member
            // (a docstring, leading fields of a script class).
            var headerEnd = Line(body.StartIndex);
            for (var i = mark; i < firstMember; i++)
            {
                headerEnd = Math.Max(headerEnd, units[i].EndLine);
            }

            units.RemoveRange(mark, firstMember - mark);
            headerEnd = Math.Max(start, Math.Min(headerEnd, units[mark].StartLine - 1));
            units.Insert(mark, new ChunkUnit(start, headerEnd, ChunkKind.TypeHeader, inner));
        }

        private void AddWhole(TsNode splitSource, int start, int end, IReadOnlyList<string> segments)
        {
            var unit = new ChunkUnit(start, end, ChunkKind.TypeHeader, segments);
            unit.AddSplitSource(() => ChildStarts(splitSource));
            units.Add(unit);
        }

        private void AddMember(TsNode node, TsNode range, IReadOnlyList<string> segments, string name, string? mergeKey)
        {
            var unit = new ChunkUnit(Line(range.StartIndex), document.LastLineOfSpan(range.StartIndex, range.EndIndex),
                ChunkKind.Member, segments, name)
            {
                MergeKey = mergeKey,
            };
            unit.AddRankedSplitSource(() => BlockStatementStarts(node));
            units.Add(unit);
        }

        private void AddDocument(TsNode range, IReadOnlyList<string> segments)
        {
            var unit = new ChunkUnit(Line(range.StartIndex), document.LastLineOfSpan(range.StartIndex, range.EndIndex),
                ChunkKind.Document, segments)
            {
                MergeKey = "document|" + string.Join("|", segments),
            };
            unit.AddRankedSplitSource(() => BlockStatementStarts(range));
            units.Add(unit);
        }

        /// <summary>The declaration inside a wrapper: its declaration or definition field, else its last named child.</summary>
        private TsNode? Inner(TsNode wrapper)
        {
            var inner = wrapper.GetChildForField("declaration") ?? wrapper.GetChildForField("definition");
            if (inner is not null)
            {
                return inner;
            }

            TsNode? last = null;
            using var cursor = new TsTreeCursor(wrapper);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    var child = cursor.CurrentNode;
                    if (child.IsNamed && !grammar.Leading.Contains(child.Type))
                    {
                        last = child;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return last;
        }

        private TsNode? Body(TsNode node)
        {
            var body = node.GetChildForField("body");
            if (body is not null || grammar.BodyTypes.Count == 0)
            {
                return body;
            }

            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    var child = cursor.CurrentNode;
                    if (child.IsNamed && grammar.BodyTypes.Contains(child.Type))
                    {
                        return child;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return null;
        }

        private string Name(TsNode node)
        {
            foreach (var field in grammar.NameFields)
            {
                if (node.GetChildForField(field) is { } named)
                {
                    return TreeSitterText.NameText(document, named);
                }
            }

            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    var child = cursor.CurrentNode;
                    if (child.IsNamed && child.GetChildForField("name") is { } named)
                    {
                        return TreeSitterText.NameText(document, named);
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return TreeSitterText.HeaderText(document, node);
        }

        private string? FunctionValueName(TsNode declaration)
        {
            using var cursor = new TsTreeCursor(declaration);
            if (!cursor.GotoFirstChild())
            {
                return null;
            }

            do
            {
                var child = cursor.CurrentNode;
                if (child.IsNamed && child.GetChildForField("value") is { } value
                    && grammar.FunctionValueTypes.Contains(value.Type)
                    && child.GetChildForField("name") is { } name)
                {
                    return TreeSitterText.NameText(document, name);
                }
            }
            while (cursor.GotoNextSibling());

            return null;
        }

        /// <summary>
        /// Start lines of the named children of every block inside a node (iterative, any depth), ranked by how many
        /// blocks enclose them, so parts break between the outermost statements when they can.
        /// </summary>
        private List<SplitPoint> BlockStatementStarts(TsNode node)
        {
            var points = new List<SplitPoint>();
            var saved = new Stack<(bool InsideBlock, int Depth)>();
            var insideBlock = false;
            var depth = 0;
            using var cursor = new TsTreeCursor(node);
            while (true)
            {
                var current = cursor.CurrentNode;
                if (insideBlock && current.IsNamed)
                {
                    points.Add(new SplitPoint(Line(current.StartIndex), depth - 1));
                }

                var isBlock = grammar.Blocks.Contains(current.Type);
                if (cursor.GotoFirstChild())
                {
                    saved.Push((insideBlock, depth));
                    insideBlock = isBlock;
                    depth += isBlock ? 1 : 0;
                    continue;
                }

                while (!cursor.GotoNextSibling())
                {
                    if (saved.Count == 0 || !cursor.GotoParent())
                    {
                        return points;
                    }

                    (insideBlock, depth) = saved.Pop();
                }
            }
        }

        private List<int> ChildStarts(TsNode node)
        {
            var lines = new List<int>();
            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    var child = cursor.CurrentNode;
                    if (child.IsNamed)
                    {
                        lines.Add(Line(child.StartIndex));
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return lines;
        }

        private int Line(int offset) => document.LineOfOffset(offset);
    }
}
