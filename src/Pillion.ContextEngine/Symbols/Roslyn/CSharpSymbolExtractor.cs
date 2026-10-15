using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Roslyn;

namespace Pillion.ContextEngine.Symbols.Roslyn;

/// <summary>
/// C# symbols from the Roslyn syntax tree (no compilation, no semantic model). Definitions: namespaces, types, enum
/// members, methods, constructors, properties, indexers, events, fields, constants, delegates and record parameters.
/// References, by name only: <c>using</c> directives (imports), base lists (inheritance), invocations and object
/// creations (calls), names in type-only positions and attributes (type uses), and other identifier names and member
/// accesses (uses), skipping locals and parameters of the enclosing member. A file with more syntax errors than the
/// chunker tolerates yields nothing and is reported as a failure.
/// </summary>
public sealed class CSharpSymbolExtractor : SyntaxSymbolExtractor
{
    private static readonly CSharpParseOptions s_parseOptions = new(LanguageVersion.Preview, DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.CSharp;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        var document = sink.Document;
        var tree = CSharpSyntaxTree.ParseText(document.Text, s_parseOptions, document.Path);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (RoslynHealth.IsBroken(root, document.LineCount, out failure))
        {
            return false;
        }

        new CSharpSymbolCollector(sink, tree.GetText(), offset => offset, []).Visit(root);
        return true;
    }
}

/// <summary>
/// Walks a C# syntax tree and adds definitions and references to a sink. Positions are mapped to the document through
/// <paramref name="mapOffset"/>, so the Razor extractor can parse a block of a .razor file on its own and still number
/// lines in the original file; signatures are read from <paramref name="source"/>, the tree's own text.
/// </summary>
internal sealed class CSharpSymbolCollector(SymbolSink sink, SourceText source, Func<int, int> mapOffset, IReadOnlyList<string> container)
    : CSharpSyntaxWalker(SyntaxWalkerDepth.Node)
{
    private readonly List<string> _containers = [.. container];
    private string _container = SymbolSink.Join(container);
    private string _from = SymbolSink.Join(container);
    private HashSet<string>? _locals;
    private HashSet<string>? _globalLocals;
    private int _memberDepth;

    /// <summary>Visits the members of a type body (the Razor extractor's entry point for <c>@code</c> blocks).</summary>
    public void VisitMembers(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            Visit(member);
        }
    }

    /// <summary>Visits statements as the body of a member named <paramref name="member"/> (Razor <c>@{ }</c> blocks).</summary>
    public void VisitBody(SyntaxNode body, string member) => InMember(member, body, () => Visit(body));

    public override void VisitGlobalStatement(GlobalStatementSyntax node)
    {
        // Top-level statements share their locals across statements.
        _globalLocals ??= CollectLocals(node.Parent?.ChildNodes().OfType<GlobalStatementSyntax>() ?? [node]);
        var saved = _locals;
        _locals = _globalLocals;
        base.VisitGlobalStatement(node);
        _locals = saved;
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.NamespaceOrType is NameSyntax name && DottedName(name) is { Length: > 0 } dotted)
        {
            Reference(dotted, SymbolReferenceKind.Import, name.SpanStart, null);
        }
    }

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) => VisitNamespace(node);

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) => VisitNamespace(node);

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, SymbolKind.Class);

    public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, SymbolKind.Struct);

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, SymbolKind.Interface);

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) =>
        VisitType(node, node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? SymbolKind.Struct : SymbolKind.Class);

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        VisitAttributes(node.AttributeLists);
        var start = HeaderStart(node, node.AttributeLists);
        Define(node.Identifier.ValueText, SymbolKind.Enum, start, node.Span.End, Signature(start, TypeHeaderEnd(node)));
        Push(node.Identifier.ValueText);
        foreach (var baseType in node.BaseList?.Types ?? default)
        {
            TypeName(baseType.Type, SymbolReferenceKind.TypeUse);
        }

        foreach (var member in node.Members)
        {
            VisitAttributes(member.AttributeLists);
            var memberStart = HeaderStart(member, member.AttributeLists);
            Define(member.Identifier.ValueText, SymbolKind.EnumMember, memberStart, member.Span.End, Signature(memberStart, member.Span.End));
            if (member.EqualsValue is not null)
            {
                InMember(member.Identifier.ValueText, member, () => Visit(member.EqualsValue));
            }
        }

        Pop();
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var start = DefineMember(node, node.Identifier.ValueText, SymbolKind.Delegate, node.SemicolonToken);
        InMember(node.Identifier.ValueText, node, () => base.VisitDelegateDeclaration(node), start >= 0);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        DefineMember(node, node.Identifier.ValueText, SymbolKind.Method, BodyStart(node));
        InMember(node.Identifier.ValueText, node, () => base.VisitMethodDeclaration(node));
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        DefineMember(node, node.Identifier.ValueText, SymbolKind.Constructor, BodyStart(node));
        InMember(node.Identifier.ValueText, node, () => base.VisitConstructorDeclaration(node));
    }

    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node) =>
        InMember("~" + node.Identifier.ValueText, node, () => base.VisitDestructorDeclaration(node));

    public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var name = "operator " + node.OperatorToken.ValueText;
        DefineMember(node, name, SymbolKind.Method, BodyStart(node));
        InMember(name, node, () => base.VisitOperatorDeclaration(node));
    }

    public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        var name = node.ImplicitOrExplicitKeyword.ValueText + " operator " + node.Type;
        DefineMember(node, name, SymbolKind.Method, BodyStart(node));
        InMember(name, node, () => base.VisitConversionOperatorDeclaration(node));
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var end = node.AccessorList?.SpanStart ?? node.ExpressionBody?.SpanStart ?? node.Span.End;
        DefineMember(node, node.Identifier.ValueText, SymbolKind.Property, end);
        InMember(node.Identifier.ValueText, node, () => base.VisitPropertyDeclaration(node));
    }

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var end = node.AccessorList?.SpanStart ?? node.ExpressionBody?.SpanStart ?? node.Span.End;
        DefineMember(node, "this[]", SymbolKind.Property, end);
        InMember("this[]", node, () => base.VisitIndexerDeclaration(node));
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        var end = node.AccessorList?.SpanStart ?? node.Span.End;
        DefineMember(node, node.Identifier.ValueText, SymbolKind.Event, end);
        InMember(node.Identifier.ValueText, node, () => base.VisitEventDeclaration(node));
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node) =>
        VisitField(node, node.Modifiers.Any(SyntaxKind.ConstKeyword) ? SymbolKind.Constant : SymbolKind.Field);

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) => VisitField(node, SymbolKind.Event);

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) =>
        // A local function is part of its member: no definition, its references come from the member.
        base.VisitLocalFunctionStatement(node);

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        switch (node.Expression)
        {
            case SimpleNameSyntax simple:
                if (simple.Identifier.ValueText != "nameof")
                {
                    Reference(simple.Identifier.ValueText, SymbolReferenceKind.Call, simple.SpanStart, null);
                    VisitTypeArguments(simple);
                }

                break;

            case MemberAccessExpressionSyntax access:
                Reference(access.Name.Identifier.ValueText, SymbolReferenceKind.Call, access.Name.SpanStart, Qualifier(access.Expression));
                Visit(access.Expression);
                VisitTypeArguments(access.Name);
                break;

            case MemberBindingExpressionSyntax binding:
                Reference(binding.Name.Identifier.ValueText, SymbolReferenceKind.Call, binding.Name.SpanStart, null);
                VisitTypeArguments(binding.Name);
                break;

            default:
                Visit(node.Expression);
                break;
        }

        Visit(node.ArgumentList);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        TypeName(node.Type, SymbolReferenceKind.Call, construction: true);
        Visit(node.ArgumentList);
        Visit(node.Initializer);
    }

    public override void VisitBaseList(BaseListSyntax node)
    {
        foreach (var baseType in node.Types)
        {
            TypeName(baseType.Type, SymbolReferenceKind.Inheritance);
            if (baseType is PrimaryConstructorBaseTypeSyntax primary)
            {
                Visit(primary.ArgumentList);
            }
        }
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        var (name, position, qualifier) = Rightmost(node.Name);
        if (name.Length > 0)
        {
            Reference(name.EndsWith("Attribute", StringComparison.Ordinal) ? name : name + "Attribute", SymbolReferenceKind.TypeUse, position, qualifier);
        }

        Visit(node.ArgumentList);
    }

    public override void VisitAttributeArgument(AttributeArgumentSyntax node) => Visit(node.Expression);

    public override void VisitTypeParameterConstraintClause(TypeParameterConstraintClauseSyntax node)
    {
        // where T : class, IComparable<T>: the constraints are type uses, the type parameter is not.
        foreach (var constraint in node.Constraints)
        {
            Visit(constraint);
        }
    }

    public override void VisitArgument(ArgumentSyntax node) => Visit(node.Expression);

    public override void VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node) => Visit(node.Expression);

    public override void VisitQualifiedName(QualifiedNameSyntax node) => Visit(node.Right);

    public override void VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => Visit(node.Name);

    public override void VisitIdentifierName(IdentifierNameSyntax node) => SimpleName(node);

    public override void VisitGenericName(GenericNameSyntax node)
    {
        SimpleName(node);
        Visit(node.TypeArgumentList);
    }

    private void VisitNamespace(BaseNamespaceDeclarationSyntax node)
    {
        var name = DottedName(node.Name);
        Define(name, SymbolKind.Namespace, node.NamespaceKeyword.SpanStart, node.Span.End, "namespace " + name);
        Push(name);
        foreach (var usingDirective in node.Usings)
        {
            Visit(usingDirective);
        }

        foreach (var member in node.Members)
        {
            Visit(member);
        }

        Pop();
    }

    private void VisitType(TypeDeclarationSyntax node, SymbolKind kind)
    {
        if (_memberDepth > 0)
        {
            return;
        }

        VisitAttributes(node.AttributeLists);
        var start = HeaderStart(node, node.AttributeLists);
        var name = node.Identifier.ValueText;
        Define(name, kind, start, node.Span.End, Signature(start, TypeHeaderEnd(node)));
        var pushed = Push(name + (node.TypeParameterList?.ToString() ?? string.Empty));

        if (node.ParameterList is { } parameters)
        {
            if (node is RecordDeclarationSyntax)
            {
                foreach (var parameter in parameters.Parameters)
                {
                    var parameterStart = HeaderStart(parameter, parameter.AttributeLists);
                    Define(parameter.Identifier.ValueText, SymbolKind.Property, parameterStart, parameter.Span.End,
                        Signature(parameterStart, parameter.Default?.SpanStart ?? parameter.Span.End));
                }
            }

            InMember(name, parameters, () => Visit(parameters));
        }

        if (node.BaseList is not null)
        {
            Visit(node.BaseList);
        }

        foreach (var clause in node.ConstraintClauses)
        {
            Visit(clause);
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

    private void VisitField(BaseFieldDeclarationSyntax node, SymbolKind kind)
    {
        VisitAttributes(node.AttributeLists);
        var modifiers = node.Modifiers.ToString();
        var type = node.Declaration.Type.ToString();
        foreach (var variable in node.Declaration.Variables)
        {
            var name = variable.Identifier.ValueText;
            if (_memberDepth == 0)
            {
                var header = string.Join(' ', modifiers, kind == SymbolKind.Event ? "event" : string.Empty, type, name);
                sink.Define(name, kind, _container, mapOffset(variable.SpanStart), mapOffset(variable.Span.End), SignatureText.Normalize(header));
            }

            InMember(name, variable, () =>
            {
                Visit(node.Declaration.Type);
                Visit(variable.Initializer);
            });
        }
    }

    /// <summary>Defines a member; returns its header start, or -1 inside another member (where nothing is defined).</summary>
    private int DefineMember(MemberDeclarationSyntax node, string name, SymbolKind kind, int headerEnd)
    {
        if (_memberDepth > 0)
        {
            return -1;
        }

        var start = HeaderStart(node, node.AttributeLists);
        Define(name, kind, start, node.Span.End, Signature(start, headerEnd));
        return start;
    }

    private int DefineMember(MemberDeclarationSyntax node, string name, SymbolKind kind, SyntaxToken end) =>
        DefineMember(node, name, kind, end.IsMissing ? node.Span.End : end.SpanStart);

    private void Define(string name, SymbolKind kind, int start, int end, string signature) =>
        sink.Define(name, kind, _container, mapOffset(start), mapOffset(end), signature);

    private void Reference(string name, SymbolReferenceKind kind, int position, string? qualifier, bool construction = false) =>
        sink.Reference(name, kind, mapOffset(position), qualifier, _from, construction);

    private void SimpleName(SimpleNameSyntax node)
    {
        var name = node.Identifier.ValueText;
        if (IsIgnored(name) || node.Parent is NameColonSyntax or NameEqualsSyntax)
        {
            return;
        }

        if (SyntaxFacts.IsInTypeOnlyContext(node))
        {
            if (_locals?.Contains(name) != true || node.Parent is QualifiedNameSyntax)
            {
                Reference(name, SymbolReferenceKind.TypeUse, node.SpanStart, NameQualifier(node));
            }

            return;
        }

        string? qualifier;
        switch (node.Parent)
        {
            case MemberAccessExpressionSyntax access when access.Name == node:
                qualifier = Qualifier(access.Expression);
                break;
            case QualifiedNameSyntax qualified when qualified.Right == node:
                qualifier = Rightmost(qualified.Left).Name;
                break;
            case MemberBindingExpressionSyntax:
                qualifier = null;
                break;
            case AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax { Parent: BaseObjectCreationExpressionSyntax creation } } assignment
                when assignment.Left == node:
                // new Order { Total = 1 }: the property of the created type.
                qualifier = creation is ObjectCreationExpressionSyntax explicitType ? Rightmost(explicitType.Type).Name : null;
                break;
            default:
                if (_locals?.Contains(name) == true)
                {
                    return;
                }

                qualifier = null;
                break;
        }

        Reference(name, SymbolReferenceKind.Use, node.SpanStart, qualifier);
    }

    /// <summary>A type written in a call or base position: one reference for its rightmost name, type uses for its type arguments.</summary>
    private void TypeName(TypeSyntax type, SymbolReferenceKind kind, bool construction = false)
    {
        switch (type)
        {
            case SimpleNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax:
                var (name, position, qualifier) = Rightmost((NameSyntax)type);
                if (name.Length > 0 && !IsIgnored(name))
                {
                    Reference(name, kind, position, qualifier, construction);
                }

                var right = type switch
                {
                    QualifiedNameSyntax qualifiedType => qualifiedType.Right,
                    AliasQualifiedNameSyntax aliasType => aliasType.Name,
                    _ => (SimpleNameSyntax)type,
                };
                VisitTypeArguments(right);
                break;
            case NullableTypeSyntax nullable:
                TypeName(nullable.ElementType, kind, construction);
                break;
            case ArrayTypeSyntax array:
                TypeName(array.ElementType, kind, construction);
                break;
            default:
                Visit(type);
                break;
        }
    }

    private void VisitTypeArguments(SimpleNameSyntax name)
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

    /// <summary>Runs <paramref name="visit"/> with <paramref name="name"/> as the member references come from.</summary>
    private void InMember(string name, SyntaxNode node, Action visit, bool isMember = true)
    {
        if (!isMember || _memberDepth > 0)
        {
            visit();
            return;
        }

        var savedFrom = _from;
        var segment = Breadcrumbs.Sanitize(name);
        _from = segment.Length == 0 ? _container : _container.Length == 0 ? segment : _container + Breadcrumbs.Separator + segment;
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

    /// <summary>Names a member's code may use without them being references: parameters, locals, type parameters.</summary>
    private static HashSet<string> CollectLocals(SyntaxNode member) => CollectLocals([member]);

    private static HashSet<string> CollectLocals(IEnumerable<SyntaxNode> members)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var ancestor in member.AncestorsAndSelf())
            {
                if (ancestor is TypeDeclarationSyntax { TypeParameterList: { } typeParameters })
                {
                    foreach (var parameter in typeParameters.Parameters)
                    {
                        names.Add(parameter.Identifier.ValueText);
                    }
                }
            }

            AddLocals(member, names);
        }

        return names;
    }

    private static void AddLocals(SyntaxNode member, HashSet<string> names)
    {
        foreach (var node in member.DescendantNodesAndSelf())
        {
            var identifier = node switch
            {
                ParameterSyntax parameter => parameter.Identifier,
                VariableDeclaratorSyntax variable when node != member => variable.Identifier,
                SingleVariableDesignationSyntax designation => designation.Identifier,
                ForEachStatementSyntax forEach => forEach.Identifier,
                CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier,
                FromClauseSyntax from => from.Identifier,
                LetClauseSyntax let => let.Identifier,
                JoinClauseSyntax join => join.Identifier,
                JoinIntoClauseSyntax into => into.Identifier,
                QueryContinuationSyntax continuation => continuation.Identifier,
                LocalFunctionStatementSyntax local => local.Identifier,
                TypeParameterSyntax typeParameter => typeParameter.Identifier,
                _ => default,
            };

            if (identifier.ValueText is { Length: > 0 } text)
            {
                names.Add(text);
            }
        }
    }

    private static bool IsIgnored(string name) =>
        name is "var" or "dynamic" or "nameof" or "_" or "value" or "unmanaged" or "notnull" or "global" or "";

    private static int HeaderStart(SyntaxNode node, SyntaxList<AttributeListSyntax> attributes)
    {
        if (attributes.Count == 0)
        {
            return node.SpanStart;
        }

        var next = attributes[^1].GetLastToken().GetNextToken();
        return next.RawKind != 0 && next.SpanStart <= node.Span.End ? next.SpanStart : node.SpanStart;
    }

    private static int TypeHeaderEnd(BaseTypeDeclarationSyntax node)
    {
        if (node.OpenBraceToken.RawKind != 0 && !node.OpenBraceToken.IsMissing)
        {
            return node.OpenBraceToken.SpanStart;
        }

        return node.SemicolonToken.IsMissing || node.SemicolonToken.RawKind == 0 ? node.Span.End : node.SemicolonToken.SpanStart;
    }

    private static int BodyStart(BaseMethodDeclarationSyntax node) =>
        node.Body?.SpanStart ?? node.ExpressionBody?.SpanStart
        ?? (node.SemicolonToken.IsMissing || node.SemicolonToken.RawKind == 0 ? node.Span.End : node.SemicolonToken.SpanStart);

    private static string DottedName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => DottedName(qualified.Left) + "." + DottedName(qualified.Right),
        AliasQualifiedNameSyntax alias => DottedName(alias.Name),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => string.Empty,
    };

    private static (string Name, int Position, string? Qualifier) Rightmost(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => (qualified.Right.Identifier.ValueText, qualified.Right.SpanStart, Rightmost(qualified.Left).Name),
        AliasQualifiedNameSyntax alias => (alias.Name.Identifier.ValueText, alias.Name.SpanStart, null),
        SimpleNameSyntax simple => (simple.Identifier.ValueText, simple.SpanStart, null),
        _ => (string.Empty, name.SpanStart, null),
    };

    private static (string Name, int Position, string? Qualifier) Rightmost(TypeSyntax type) =>
        type is NameSyntax name ? Rightmost(name) : (string.Empty, type.SpanStart, null);

    private static string? NameQualifier(SimpleNameSyntax node) =>
        node.Parent is QualifiedNameSyntax qualified && qualified.Right == node ? Rightmost(qualified.Left).Name : null;

    /// <summary>
    /// The simple name an access is made through: a type, namespace or variable name, or the type of
    /// <c>new Order().Total</c>; null for <c>this</c>, <c>base</c> and other expressions. A variable's name is kept: it
    /// tells the resolver the member is not on the current type, even though it names no container.
    /// </summary>
    private static string? Qualifier(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        ObjectCreationExpressionSyntax creation => Rightmost(creation.Type).Name is { Length: > 0 } type ? type : null,
        ParenthesizedExpressionSyntax parenthesized => Qualifier(parenthesized.Expression),
        _ => null,
    };
}
