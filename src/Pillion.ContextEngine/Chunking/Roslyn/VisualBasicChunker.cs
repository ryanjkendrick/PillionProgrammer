using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking.Roslyn;

/// <summary>
/// Visual Basic by Roslyn syntax tree (no compilation), with the same shape as <see cref="CSharpChunker"/>: members
/// with their doc comments and attributes, type headers (the declaration with its <c>Inherits</c> and
/// <c>Implements</c> lines), namespaces in the breadcrumb, <c>Option</c> and <c>Imports</c> as document chunks.
/// </summary>
public sealed class VisualBasicChunker : SyntaxChunker
{
    private static readonly VisualBasicParseOptions s_parseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.VisualBasic;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, out string? failure)
    {
        var document = budget.Document;
        var tree = VisualBasicSyntaxTree.ParseText(document.Text, s_parseOptions, document.Path);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (RoslynHealth.IsBroken(root, document.LineCount, out failure))
        {
            return null;
        }

        var units = new List<ChunkUnit>();
        var collector = new Collector(document, units);
        foreach (var node in root.Options)
        {
            collector.AddDocument(node, []);
        }

        foreach (var node in root.Imports)
        {
            collector.AddDocument(node, []);
        }

        foreach (var node in root.Attributes)
        {
            collector.AddDocument(node, []);
        }

        collector.VisitMembers(root.Members, []);
        return units;
    }

    private sealed class Collector(SourceDocument document, List<ChunkUnit> units)
    {
        public void VisitMembers(SyntaxList<StatementSyntax> members, IReadOnlyList<string> container)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case NamespaceBlockSyntax ns:
                        VisitMembers(ns.Members, [.. container, ns.NamespaceStatement.Name.ToString()]);
                        break;

                    case EnumBlockSyntax enumBlock:
                        var enumUnit = Add(enumBlock, ChunkKind.TypeHeader, [.. container, enumBlock.EnumStatement.Identifier.ValueText], null);
                        enumUnit.AddSplitSource(() => enumBlock.Members.Select(m => Line(m.SpanStart)));
                        break;

                    case TypeBlockSyntax type:
                        VisitType(type, container);
                        break;

                    case FieldDeclarationSyntax field:
                        var names = field.Declarators.SelectMany(d => d.Names).Select(n => n.Identifier.ValueText);
                        var fieldUnit = Add(field, ChunkKind.Member, container, string.Join(", ", names));
                        fieldUnit.AddSplitSource(() => StatementStarts(field));
                        break;

                    case MethodBlockBaseSyntax or PropertyBlockSyntax or EventBlockSyntax or MethodBaseSyntax:
                        var unit = Add(member, ChunkKind.Member, container, NameOf(member));
                        unit.AddSplitSource(() => StatementStarts(member));
                        break;

                    default:
                        AddDocument(member, container);
                        break;
                }
            }
        }

        private void VisitType(TypeBlockSyntax type, IReadOnlyList<string> container)
        {
            var statement = type.BlockStatement;
            var name = statement.Identifier.ValueText + (statement.TypeParameterList?.ToString() ?? string.Empty);
            IReadOnlyList<string> inner = [.. container, name];
            if (type.Members.Count == 0)
            {
                Add(type, ChunkKind.TypeHeader, inner, null);
                return;
            }

            // The header is the declaration line(s) plus the Inherits and Implements statements that follow it.
            var last = new SyntaxNode[] { statement }
                .Concat(type.Inherits)
                .Concat(type.Implements)
                .MaxBy(n => n.Span.End)!;
            units.Add(new ChunkUnit(Line(type.SpanStart), LastLine(last), ChunkKind.TypeHeader, inner));
            VisitMembers(type.Members, inner);
        }

        public void AddDocument(SyntaxNode node, IReadOnlyList<string> container) =>
            units.Add(new ChunkUnit(Line(node.SpanStart), LastLine(node), ChunkKind.Document, container)
            {
                MergeKey = "document|" + string.Join("|", container),
            });

        private ChunkUnit Add(SyntaxNode node, ChunkKind kind, IReadOnlyList<string> container, string? name)
        {
            var unit = new ChunkUnit(Line(node.SpanStart), LastLine(node), kind, container, name)
            {
                MergeKey = node is FieldDeclarationSyntax ? "fields|" + string.Join("|", container) : null,
            };
            units.Add(unit);
            return unit;
        }

        private IEnumerable<int> StatementStarts(SyntaxNode node) => node
            .DescendantNodes()
            .Where(n => n is ExecutableStatementSyntax or AccessorBlockSyntax or CaseBlockSyntax
                or ExpressionSyntax { Parent: CollectionInitializerSyntax })
            .Select(n => Line(n.SpanStart));

        private static string NameOf(StatementSyntax member)
        {
            var statement = member switch
            {
                MethodBlockBaseSyntax block => block.BlockStatement,
                PropertyBlockSyntax property => property.PropertyStatement,
                EventBlockSyntax eventBlock => eventBlock.EventStatement,
                _ => member,
            };

            return statement switch
            {
                MethodStatementSyntax method => method.Identifier.ValueText + (method.TypeParameterList?.ToString() ?? string.Empty),
                SubNewStatementSyntax => "New",
                OperatorStatementSyntax op => "Operator " + op.OperatorToken.ValueText,
                PropertyStatementSyntax property => property.Identifier.ValueText,
                EventStatementSyntax eventStatement => eventStatement.Identifier.ValueText,
                DeclareStatementSyntax declare => declare.Identifier.ValueText,
                DelegateStatementSyntax @delegate => @delegate.Identifier.ValueText,
                AccessorStatementSyntax accessor => accessor.AccessorKeyword.ValueText,
                _ => statement.Kind().ToString(),
            };
        }

        private int Line(int position) => document.LineOfOffset(position);

        private int LastLine(SyntaxNode node) => document.LastLineOfSpan(node.SpanStart, node.Span.End);
    }
}
