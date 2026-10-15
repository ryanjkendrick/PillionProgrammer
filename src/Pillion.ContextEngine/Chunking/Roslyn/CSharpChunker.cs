using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking.Roslyn;

/// <summary>
/// C# by Roslyn syntax tree (no compilation): one chunk per member with its doc comment and attributes, type headers
/// as their own chunks, namespaces and enclosing types in the breadcrumb, using directives and top-level statements as
/// document chunks, consecutive fields grouped. Members over the cap are split at statement boundaries.
/// </summary>
public sealed class CSharpChunker : SyntaxChunker
{
    private static readonly CSharpParseOptions s_parseOptions = new(LanguageVersion.Preview, DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.CSharp;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, out string? failure)
    {
        var document = budget.Document;
        var tree = CSharpSyntaxTree.ParseText(document.Text, s_parseOptions, document.Path);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (RoslynHealth.IsBroken(root, document.LineCount, out failure))
        {
            return null;
        }

        var units = new List<ChunkUnit>();
        new CSharpUnitCollector(document, offset => offset, units).VisitCompilationUnit(root);
        return units;
    }
}

/// <summary>
/// Walks a C# syntax tree and emits units. Positions in the tree are mapped to the document through
/// <paramref name="mapOffset"/>, so the Razor chunker can parse a block of a .razor file on its own and still number
/// lines in the original file.
/// </summary>
internal sealed class CSharpUnitCollector(SourceDocument document, Func<int, int> mapOffset, List<ChunkUnit> units)
{
    public void VisitCompilationUnit(CompilationUnitSyntax root)
    {
        foreach (var node in root.Externs)
        {
            AddDocument(node, []);
        }

        foreach (var node in root.Usings)
        {
            AddDocument(node, []);
        }

        foreach (var node in root.AttributeLists)
        {
            AddDocument(node, []);
        }

        VisitMembers(root.Members, []);
    }

    public void VisitMembers(SyntaxList<MemberDeclarationSyntax> members, IReadOnlyList<string> container)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    var inner = Append(container, ns.Name.ToString());
                    foreach (var node in ns.Externs)
                    {
                        AddDocument(node, inner);
                    }

                    foreach (var node in ns.Usings)
                    {
                        AddDocument(node, inner);
                    }

                    VisitMembers(ns.Members, inner);
                    break;

                case EnumDeclarationSyntax enumDeclaration:
                    var enumUnit = Add(enumDeclaration, ChunkKind.TypeHeader, Append(container, enumDeclaration.Identifier.ValueText));
                    enumUnit.AddSplitSource(() => enumDeclaration.Members.Select(m => Line(m.SpanStart)));
                    break;

                case TypeDeclarationSyntax type:
                    VisitType(type, container);
                    break;

                case GlobalStatementSyntax or IncompleteMemberSyntax:
                    var statementUnit = AddDocument(member, container);
                    statementUnit.AddSplitSource(() => StatementStarts(member));
                    break;

                case BaseFieldDeclarationSyntax field:
                    AddMember(field, container, string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
                        mergeKey: "fields|" + string.Join("|", container));
                    break;

                default:
                    AddMember(member, container, NameOf(member), mergeKey: null);
                    break;
            }
        }
    }

    private void VisitType(TypeDeclarationSyntax type, IReadOnlyList<string> container)
    {
        var name = type.Identifier.ValueText + (type.TypeParameterList?.ToString() ?? string.Empty);
        var inner = Append(container, name);
        if (type.Members.Count == 0)
        {
            Add(type, ChunkKind.TypeHeader, inner);
            return;
        }

        var headerStart = Line(type.SpanStart);
        var headerEnd = type.OpenBraceToken.IsMissing
            ? Line(type.Members[0].SpanStart) - 1
            : Line(type.OpenBraceToken.SpanStart);
        units.Add(new ChunkUnit(headerStart, Math.Max(headerStart, headerEnd), ChunkKind.TypeHeader, inner));
        VisitMembers(type.Members, inner);
    }

    private ChunkUnit AddMember(MemberDeclarationSyntax member, IReadOnlyList<string> container, string name, string? mergeKey)
    {
        var unit = new ChunkUnit(Line(member.SpanStart), LastLine(member), ChunkKind.Member, container, name)
        {
            MergeKey = mergeKey,
        };
        unit.AddSplitSource(() => StatementStarts(member));
        units.Add(unit);
        return unit;
    }

    private ChunkUnit AddDocument(SyntaxNode node, IReadOnlyList<string> container)
    {
        var unit = new ChunkUnit(Line(node.SpanStart), LastLine(node), ChunkKind.Document, container)
        {
            MergeKey = "document|" + string.Join("|", container),
        };
        units.Add(unit);
        return unit;
    }

    private ChunkUnit Add(SyntaxNode node, ChunkKind kind, IReadOnlyList<string> segments)
    {
        var unit = new ChunkUnit(Line(node.SpanStart), LastLine(node), kind, segments);
        units.Add(unit);
        return unit;
    }

    /// <summary>Lines where a split part may begin: statements, accessors, switch sections and initializer elements.</summary>
    private IEnumerable<int> StatementStarts(SyntaxNode node) => node
        .DescendantNodes()
        .Where(n => n is StatementSyntax and not BlockSyntax
            or AccessorDeclarationSyntax
            or SwitchSectionSyntax
            or SwitchExpressionArmSyntax
            or ExpressionSyntax { Parent: InitializerExpressionSyntax }
            or CollectionElementSyntax
            or MemberDeclarationSyntax)
        .Select(n => Line(n.SpanStart));

    private static string NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText + (m.TypeParameterList?.ToString() ?? string.Empty),
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        DestructorDeclarationSyntax d => "~" + d.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax e => e.Identifier.ValueText,
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.ValueText,
        ConversionOperatorDeclarationSyntax c => c.ImplicitOrExplicitKeyword.ValueText + " operator " + c.Type,
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };

    private int Line(int position) => document.LineOfOffset(mapOffset(position));

    private int LastLine(SyntaxNode node) =>
        document.LineOfOffset(mapOffset(node.Span.End > node.SpanStart ? node.Span.End - 1 : node.SpanStart));

    private static IReadOnlyList<string> Append(IReadOnlyList<string> container, string name) => [.. container, name];
}
