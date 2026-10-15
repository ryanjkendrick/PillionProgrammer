using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pillion.ContextEngine.Chunking.Core;
using Pillion.ContextEngine.Chunking.Roslyn;
using Pillion.ContextEngine.Chunking.TreeSitter;

namespace Pillion.ContextEngine.Chunking.Razor;

/// <summary>
/// Razor components and views (<c>.razor</c>, <c>.cshtml</c>), split into their C# and markup parts. The members of
/// <c>@code</c> and <c>@functions</c> blocks are chunked by the C# chunker (breadcrumb
/// <c>path &gt; Component &gt; member</c>, where the component is the file name); an <c>@{ }</c> block is one unit split
/// at its statements; the markup, with the code blocks blanked out, is chunked as HTML, or by regions of lines when
/// the HTML grammar is not available.
/// </summary>
public sealed class RazorChunker : SyntaxChunker
{
    private static readonly CSharpParseOptions s_parseOptions = new(LanguageVersion.Preview, Microsoft.CodeAnalysis.DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.Razor;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, ResourceScope scope, out string? failure)
    {
        failure = null;
        var document = budget.Document;
        var text = document.Text;
        var blocks = RazorScanner.Scan(text);
        var component = ComponentName(document.Path);
        IReadOnlyList<string> container = [component];
        var units = new List<ChunkUnit>();

        foreach (var block in blocks)
        {
            if (block.Kind == RazorBlockKind.Members)
            {
                AddMembers(document, block, container, units);
            }
            else
            {
                AddStatements(document, block, container, units);
            }
        }

        // Markup: the same text with every code block replaced by spaces (line breaks kept, so offsets and lines match).
        var masked = Mask(text, blocks);
        var markup = HtmlChunker.CollectMarkup(HtmlChunker.Loader, budget, masked, maxErrorFraction: 1.0, scope, out _);
        if (markup is not null)
        {
            units.AddRange(markup);
        }
        else
        {
            AddMarkupRegions(document, blocks, units);
        }

        return units;
    }

    private static void AddMembers(SourceDocument document, RazorBlock block, IReadOnlyList<string> container, List<ChunkUnit> units)
    {
        // Parse the block's content as the body of a class; map positions back to the file.
        const string Prefix = "class __RazorBlock {";
        var contentStart = block.Open + 1;
        var contentLength = Math.Max(0, block.Close - contentStart);
        var source = string.Concat(Prefix, document.Text.AsSpan(contentStart, contentLength), "\n}");
        var tree = CSharpSyntaxTree.ParseText(source, s_parseOptions);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (root.Members.FirstOrDefault() is not ClassDeclarationSyntax wrapper)
        {
            return;
        }

        int Map(int position) => position < Prefix.Length
            ? block.Open
            : Math.Min(contentStart + (position - Prefix.Length), Math.Max(block.Close, contentStart));

        new CSharpUnitCollector(document, Map, units).VisitMembers(wrapper.Members, container);
    }

    private static void AddStatements(SourceDocument document, RazorBlock block, IReadOnlyList<string> container, List<ChunkUnit> units)
    {
        var start = document.LineOfOffset(block.Start);
        var end = document.LineOfOffset(block.Close);
        var unit = new ChunkUnit(start, end, ChunkKind.Member, container, "@{ }");
        unit.AddRankedSplitSource(() =>
        {
            const string Prefix = "class __RazorBlock { void __Render() {";
            var contentStart = block.Open + 1;
            var contentLength = Math.Max(0, block.Close - contentStart);
            var source = string.Concat(Prefix, document.Text.AsSpan(contentStart, contentLength), "\n}}");
            var root = CSharpSyntaxTree.ParseText(source, s_parseOptions).GetRoot();
            return root.DescendantNodes()
                .OfType<StatementSyntax>()
                .Where(s => s is not BlockSyntax && s.SpanStart >= Prefix.Length)
                .Select(s => new SplitPoint(
                    document.LineOfOffset(contentStart + (s.SpanStart - Prefix.Length)),
                    s.Ancestors().Count(a => a is StatementSyntax and not BlockSyntax)));
        });
        units.Add(unit);
    }

    /// <summary>Without the HTML grammar: one markup unit per run of lines outside code blocks, grouped.</summary>
    private static void AddMarkupRegions(SourceDocument document, List<RazorBlock> blocks, List<ChunkUnit> units)
    {
        var position = 0;
        foreach (var block in blocks.Append(new RazorBlock(RazorBlockKind.Statements, document.Text.Length, document.Text.Length, document.Text.Length)))
        {
            if (block.Start > position && !document.Text.AsSpan(position, block.Start - position).IsWhiteSpace())
            {
                var start = document.LineOfOffset(position);
                var end = document.LastLineOfSpan(position, block.Start);
                var unit = new ChunkUnit(start, end, ChunkKind.Markup, []) { MergeKey = "markup|" };
                unit.AddSplitSource(() => Enumerable.Range(start, end - start + 1));
                units.Add(unit);
            }

            position = Math.Min(document.Text.Length, block.Close + 1);
        }
    }

    private static string Mask(string text, List<RazorBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        foreach (var block in blocks)
        {
            var end = Math.Min(block.Close, chars.Length - 1);
            for (var i = block.Start; i <= end; i++)
            {
                if (chars[i] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029'))
                {
                    chars[i] = ' ';
                }
            }
        }

        return new string(chars);
    }

    private static string ComponentName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "component" : name;
    }
}
