using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Roslyn;

namespace Pillion.ContextEngine.Symbols.Roslyn;

/// <summary>
/// Visual Basic symbols from the Roslyn syntax tree (no compilation), with the same rules as
/// <see cref="CSharpSymbolExtractor"/>: namespaces, classes, structures, interfaces, modules, enums and their members,
/// methods, constructors, operators, properties, events, fields, constants, delegates and <c>Declare</c> statements as
/// definitions; <c>Imports</c>, <c>Inherits</c> and <c>Implements</c>, invocations, <c>New</c>, type-only positions,
/// attributes, member accesses and other identifier names as references. Names are compared as written, although
/// Visual Basic itself ignores case.
/// </summary>
public sealed class VisualBasicSymbolExtractor : SyntaxSymbolExtractor
{
    private static readonly VisualBasicParseOptions s_parseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.VisualBasic;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        var document = sink.Document;
        var tree = VisualBasicSyntaxTree.ParseText(document.Text, s_parseOptions, document.Path);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (RoslynHealth.IsBroken(root, document.LineCount, out failure))
        {
            return false;
        }

        new Collector(sink, tree.GetText()).Visit(root);
        return true;
    }

    private sealed class Collector(SymbolSink sink, SourceText source) : VisualBasicSyntaxWalker(SyntaxWalkerDepth.Node)
    {
        private readonly List<string> _containers = [];
        private string _container = string.Empty;
        private string _from = string.Empty;
        private HashSet<string>? _locals;
        private int _memberDepth;

        public override void VisitImportsStatement(ImportsStatementSyntax node)
        {
            foreach (var clause in node.ImportsClauses)
            {
                if (clause is SimpleImportsClauseSyntax simple && DottedName(simple.Name) is { Length: > 0 } dotted)
                {
                    Reference(dotted, SymbolReferenceKind.Import, simple.Name.SpanStart, null);
                }
            }
        }

        public override void VisitNamespaceBlock(NamespaceBlockSyntax node)
        {
            var name = DottedName(node.NamespaceStatement.Name);
            Define(name, SymbolKind.Namespace, node.SpanStart, node.Span.End, "Namespace " + name);
            Push(name);
            foreach (var member in node.Members)
            {
                Visit(member);
            }

            Pop();
        }

        public override void VisitClassBlock(ClassBlockSyntax node) => VisitType(node, SymbolKind.Class);

        public override void VisitStructureBlock(StructureBlockSyntax node) => VisitType(node, SymbolKind.Struct);

        public override void VisitInterfaceBlock(InterfaceBlockSyntax node) => VisitType(node, SymbolKind.Interface);

        public override void VisitModuleBlock(ModuleBlockSyntax node) => VisitType(node, SymbolKind.Module);

        public override void VisitEnumBlock(EnumBlockSyntax node)
        {
            var statement = node.EnumStatement;
            VisitAttributes(statement.AttributeLists);
            var start = HeaderStart(statement, statement.AttributeLists);
            Define(statement.Identifier.ValueText, SymbolKind.Enum, start, node.Span.End, Signature(start, statement.Span.End));
            Push(statement.Identifier.ValueText);
            foreach (var member in node.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    var memberStart = HeaderStart(enumMember, enumMember.AttributeLists);
                    Define(enumMember.Identifier.ValueText, SymbolKind.EnumMember, memberStart, enumMember.Span.End,
                        Signature(memberStart, enumMember.Span.End));
                    if (enumMember.Initializer is not null)
                    {
                        InMember(enumMember.Identifier.ValueText, enumMember, () => Visit(enumMember.Initializer));
                    }
                }
            }

            Pop();
        }

        public override void VisitMethodBlock(MethodBlockSyntax node) => VisitMethod(node, node.SubOrFunctionStatement,
            node.SubOrFunctionStatement.Identifier.ValueText, SymbolKind.Method);

        public override void VisitConstructorBlock(ConstructorBlockSyntax node) =>
            VisitMethod(node, node.SubNewStatement, "New", SymbolKind.Constructor);

        public override void VisitOperatorBlock(OperatorBlockSyntax node) =>
            VisitMethod(node, node.OperatorStatement, "Operator " + node.OperatorStatement.OperatorToken.ValueText, SymbolKind.Method);

        public override void VisitMethodStatement(MethodStatementSyntax node)
        {
            if (node.Parent is MethodBlockBaseSyntax)
            {
                base.VisitMethodStatement(node);
                return;
            }

            // A method without a body (an interface member, MustOverride).
            VisitMethod(node, node, node.Identifier.ValueText, SymbolKind.Method);
        }

        public override void VisitDeclareStatement(DeclareStatementSyntax node) =>
            VisitMethod(node, node, node.Identifier.ValueText, SymbolKind.Function);

        public override void VisitDelegateStatement(DelegateStatementSyntax node) =>
            VisitMethod(node, node, node.Identifier.ValueText, SymbolKind.Delegate);

        public override void VisitPropertyBlock(PropertyBlockSyntax node) =>
            VisitMethod(node, node.PropertyStatement, node.PropertyStatement.Identifier.ValueText, SymbolKind.Property);

        public override void VisitPropertyStatement(PropertyStatementSyntax node)
        {
            if (node.Parent is PropertyBlockSyntax)
            {
                base.VisitPropertyStatement(node);
                return;
            }

            VisitMethod(node, node, node.Identifier.ValueText, SymbolKind.Property);
        }

        public override void VisitEventBlock(EventBlockSyntax node) =>
            VisitMethod(node, node.EventStatement, node.EventStatement.Identifier.ValueText, SymbolKind.Event);

        public override void VisitEventStatement(EventStatementSyntax node)
        {
            if (node.Parent is EventBlockSyntax)
            {
                base.VisitEventStatement(node);
                return;
            }

            VisitMethod(node, node, node.Identifier.ValueText, SymbolKind.Event);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            VisitAttributes(node.AttributeLists);
            var kind = node.Modifiers.Any(SyntaxKind.ConstKeyword) ? SymbolKind.Constant : SymbolKind.Field;
            var modifiers = node.Modifiers.ToString();
            foreach (var declarator in node.Declarators)
            {
                var asClause = declarator.AsClause?.ToString() ?? string.Empty;
                foreach (var name in declarator.Names)
                {
                    var text = name.Identifier.ValueText;
                    if (_memberDepth == 0)
                    {
                        sink.Define(text, kind, _container, name.SpanStart, declarator.Span.End,
                            SignatureText.Normalize(string.Join(' ', modifiers, text, asClause)));
                    }

                    InMember(text, declarator, () =>
                    {
                        Visit(declarator.AsClause);
                        Visit(declarator.Initializer);
                    });
                }
            }
        }

        public override void VisitInheritsStatement(InheritsStatementSyntax node)
        {
            foreach (var type in node.Types)
            {
                TypeName(type, SymbolReferenceKind.Inheritance);
            }
        }

        public override void VisitImplementsStatement(ImplementsStatementSyntax node)
        {
            foreach (var type in node.Types)
            {
                TypeName(type, SymbolReferenceKind.Inheritance);
            }
        }

        public override void VisitImplementsClause(ImplementsClauseSyntax node)
        {
            // Implements IDisposable.Dispose: the interface member this member implements.
            foreach (var member in node.InterfaceMembers)
            {
                Reference(member.Right.Identifier.ValueText, SymbolReferenceKind.Use, member.Right.SpanStart, Rightmost(member.Left).Name);
            }
        }

        public override void VisitHandlesClause(HandlesClauseSyntax node)
        {
            // Handles Button1.Click: event names, not worth a reference each.
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            switch (node.Expression)
            {
                case SimpleNameSyntax simple:
                    Reference(simple.Identifier.ValueText, SymbolReferenceKind.Call, simple.SpanStart, null);
                    VisitTypeArguments(simple);
                    break;

                case MemberAccessExpressionSyntax access:
                    Reference(access.Name.Identifier.ValueText, SymbolReferenceKind.Call, access.Name.SpanStart,
                        access.Expression is null ? null : Qualifier(access.Expression));
                    Visit(access.Expression);
                    VisitTypeArguments(access.Name);
                    break;

                case null:
                    break;

                default:
                    Visit(node.Expression);
                    break;
            }

            Visit(node.ArgumentList);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            TypeName(node.Type, SymbolReferenceKind.Call);
            Visit(node.ArgumentList);
            Visit(node.Initializer);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            var (name, position, qualifier) = node.Name is NameSyntax attributeName ? Rightmost(attributeName) : (string.Empty, 0, null);
            if (name.Length > 0)
            {
                Reference(name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase) ? name : name + "Attribute",
                    SymbolReferenceKind.TypeUse, position, qualifier);
            }

            Visit(node.ArgumentList);
        }

        public override void VisitSimpleArgument(SimpleArgumentSyntax node) => Visit(node.Expression);

        public override void VisitQualifiedName(QualifiedNameSyntax node) => Visit(node.Right);

        public override void VisitIdentifierName(IdentifierNameSyntax node) => SimpleName(node);

        public override void VisitGenericName(GenericNameSyntax node)
        {
            SimpleName(node);
            Visit(node.TypeArgumentList);
        }

        private void VisitType(TypeBlockSyntax node, SymbolKind kind)
        {
            if (_memberDepth > 0)
            {
                return;
            }

            var statement = node.BlockStatement;
            VisitAttributes(statement.AttributeLists);
            var start = HeaderStart(statement, statement.AttributeLists);
            var name = statement.Identifier.ValueText;
            Define(name, kind, start, node.Span.End, Signature(start, statement.Span.End));
            var pushed = Push(name + (statement.TypeParameterList?.ToString() ?? string.Empty));
            foreach (var inherits in node.Inherits)
            {
                Visit(inherits);
            }

            foreach (var implements in node.Implements)
            {
                Visit(implements);
            }

            foreach (var member in node.Members)
            {
                Visit(member);
            }

            if (pushed)
            {
                Pop();
            }
        }

        private void VisitMethod(SyntaxNode node, DeclarationStatementSyntax statement, string name, SymbolKind kind)
        {
            var attributes = statement switch
            {
                MethodBaseSyntax method => method.AttributeLists,
                _ => default,
            };

            if (_memberDepth == 0)
            {
                var start = HeaderStart(statement, attributes);
                Define(name, kind, start, node.Span.End, Signature(start, statement.Span.End));
            }

            InMember(name, node, () => VisitChildren(node));
        }

        private void VisitChildren(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes())
            {
                Visit(child);
            }
        }

        private void Define(string name, SymbolKind kind, int start, int end, string signature) =>
            sink.Define(name, kind, _container, start, end, signature);

        private void Reference(string name, SymbolReferenceKind kind, int position, string? qualifier) =>
            sink.Reference(name, kind, position, qualifier, _from);

        private void SimpleName(SimpleNameSyntax node)
        {
            var name = node.Identifier.ValueText;
            if (name.Length == 0 || node.Parent is NameColonEqualsSyntax)
            {
                return;
            }

            if (SyntaxFacts.IsInTypeOnlyContext(node))
            {
                if (_locals?.Contains(name) != true || node.Parent is QualifiedNameSyntax)
                {
                    var qualifier = node.Parent is QualifiedNameSyntax q && q.Right == node ? Rightmost(q.Left).Name : null;
                    Reference(name, SymbolReferenceKind.TypeUse, node.SpanStart, qualifier);
                }

                return;
            }

            string? memberQualifier;
            switch (node.Parent)
            {
                case MemberAccessExpressionSyntax access when access.Name == node:
                    memberQualifier = access.Expression is null ? null : Qualifier(access.Expression);
                    break;
                case QualifiedNameSyntax qualified when qualified.Right == node:
                    memberQualifier = Rightmost(qualified.Left).Name;
                    break;
                default:
                    if (_locals?.Contains(name) == true)
                    {
                        return;
                    }

                    memberQualifier = null;
                    break;
            }

            Reference(name, SymbolReferenceKind.Use, node.SpanStart, memberQualifier);
        }

        private void TypeName(TypeSyntax type, SymbolReferenceKind kind)
        {
            if (type is NameSyntax name)
            {
                var (text, position, qualifier) = Rightmost(name);
                if (text.Length > 0)
                {
                    Reference(text, kind, position, qualifier);
                }

                VisitTypeArguments(name switch
                {
                    QualifiedNameSyntax qualified => qualified.Right,
                    SimpleNameSyntax simple => simple,
                    _ => null,
                });
            }
            else if (type is NullableTypeSyntax nullable)
            {
                TypeName(nullable.ElementType, kind);
            }
            else if (type is ArrayTypeSyntax array)
            {
                TypeName(array.ElementType, kind);
            }
            else
            {
                Visit(type);
            }
        }

        private void VisitTypeArguments(SimpleNameSyntax? name)
        {
            if (name is GenericNameSyntax generic)
            {
                Visit(generic.TypeArgumentList);
            }
        }

        private void VisitAttributes(SyntaxList<AttributeListSyntax> lists)
        {
            foreach (var list in lists)
            {
                Visit(list);
            }
        }

        private void InMember(string name, SyntaxNode node, Action visit)
        {
            if (_memberDepth > 0)
            {
                visit();
                return;
            }

            var savedFrom = _from;
            _from = _container.Length == 0 ? name : _container + Breadcrumbs.Separator + Breadcrumbs.Sanitize(name);
            _locals = CollectLocals(node);
            _memberDepth++;
            try
            {
                visit();
            }
            finally
            {
                _memberDepth--;
                _locals = null;
                _from = savedFrom;
            }
        }

        private bool Push(string segment)
        {
            var clean = Breadcrumbs.Sanitize(segment);
            if (clean.Length == 0)
            {
                return false;
            }

            _containers.Add(clean);
            _container = SymbolSink.Join(_containers);
            _from = _container;
            return true;
        }

        private void Pop()
        {
            _containers.RemoveAt(_containers.Count - 1);
            _container = SymbolSink.Join(_containers);
            _from = _container;
        }

        private string Signature(int start, int end)
        {
            start = Math.Clamp(start, 0, source.Length);
            end = Math.Clamp(end, start, Math.Min(source.Length, start + (SignatureText.MaxLength * 4)));
            return SignatureText.Normalize(source.ToString(TextSpan.FromBounds(start, end)));
        }

        private string? Qualifier(ExpressionSyntax expression)
        {
            var qualifier = expression switch
            {
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                _ => null,
            };

            return qualifier is null || _locals?.Contains(qualifier) == true ? null : qualifier;
        }

        private static HashSet<string> CollectLocals(SyntaxNode member)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ancestor in member.AncestorsAndSelf())
            {
                if (ancestor is TypeBlockSyntax { BlockStatement.TypeParameterList: { } typeParameters })
                {
                    foreach (var parameter in typeParameters.Parameters)
                    {
                        names.Add(parameter.Identifier.ValueText);
                    }
                }
            }

            foreach (var node in member.DescendantNodesAndSelf())
            {
                var identifier = node switch
                {
                    ModifiedIdentifierSyntax modified when modified.Parent?.Parent is not FieldDeclarationSyntax => modified.Identifier,
                    TypeParameterSyntax typeParameter => typeParameter.Identifier,
                    CatchStatementSyntax { IdentifierName: { } caught } => caught.Identifier,
                    ForStatementSyntax { ControlVariable: IdentifierNameSyntax control } => control.Identifier,
                    ForEachStatementSyntax { ControlVariable: IdentifierNameSyntax control } => control.Identifier,
                    _ => default,
                };

                if (identifier.ValueText is { Length: > 0 } text)
                {
                    names.Add(text);
                }
            }

            return names;
        }

        private static int HeaderStart(SyntaxNode node, SyntaxList<AttributeListSyntax> attributes)
        {
            if (attributes.Count == 0)
            {
                return node.SpanStart;
            }

            var next = attributes[^1].GetLastToken().GetNextToken();
            return next.RawKind != 0 && next.SpanStart <= node.Span.End ? next.SpanStart : node.SpanStart;
        }

        private static string DottedName(NameSyntax name) => name switch
        {
            QualifiedNameSyntax qualified => DottedName(qualified.Left) + "." + DottedName(qualified.Right),
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty,
        };

        private static (string Name, int Position, string? Qualifier) Rightmost(NameSyntax name) => name switch
        {
            QualifiedNameSyntax qualified => (qualified.Right.Identifier.ValueText, qualified.Right.SpanStart, Rightmost(qualified.Left).Name),
            SimpleNameSyntax simple => (simple.Identifier.ValueText, simple.SpanStart, null),
            _ => (string.Empty, name.SpanStart, null),
        };
    }
}
