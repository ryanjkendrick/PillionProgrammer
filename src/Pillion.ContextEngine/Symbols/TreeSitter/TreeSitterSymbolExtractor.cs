using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.TreeSitter;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsTreeCursor = TreeSitter.TreeCursor;

namespace Pillion.ContextEngine.Symbols.TreeSitter;

/// <summary>
/// Symbols for any tree-sitter grammar described by a <see cref="TreeSitterSymbolGrammar"/>: one iterative walk over
/// the tree that keeps a stack of contexts (the container breadcrumb, whether it is inside a type or a function, and
/// whether names are currently inheritance, imports or type uses). A grammar that cannot be loaded or a parse with too
/// much of the text inside error nodes yields nothing and is reported as a failure.
/// </summary>
public sealed class TreeSitterSymbolExtractor : SyntaxSymbolExtractor
{
    private readonly GrammarLoader _loader;
    private readonly Lazy<CompiledGrammar?> _compiled;

    /// <summary>Creates an extractor for a grammar.</summary>
    /// <param name="grammar">The symbol description.</param>
    public TreeSitterSymbolExtractor(TreeSitterSymbolGrammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        Grammar = grammar;
        _loader = GrammarLoader.For(grammar.Grammar.GrammarId);
        _compiled = new Lazy<CompiledGrammar?>(
            () => _loader.Language is { } language ? new CompiledGrammar(grammar, language) : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The symbol description.</summary>
    public TreeSitterSymbolGrammar Grammar { get; }

    /// <inheritdoc />
    public override string Language => Grammar.Language;

    /// <summary>True when the grammar's native library loads on this machine.</summary>
    public bool IsAvailable => _loader.Language is not null;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        using var tree = TreeSitterChunker.Parse(_loader, sink.Document.Text, Grammar.Grammar.MaxErrorFraction, out failure);
        if (tree is null || _compiled.Value is not { } compiled)
        {
            failure ??= _loader.Error ?? "The grammar is not available.";
            return false;
        }

        new Walker(compiled, sink).Run(tree.RootNode);
        return true;
    }

    private enum Mode
    {
        Normal,
        Inheritance,
        Import,
        TypeUse,
    }

    private enum Scope
    {
        None,
        Type,
        Function,
    }

    /// <summary>What a node type means to the walker.</summary>
    private sealed class NodeRole
    {
        public TreeSitterDefinitionRule? Definition { get; set; }

        public ushort RequiredField { get; set; }

        public ushort KindField { get; set; }

        public ushort ContainerField { get; set; }

        public ushort[]? NameFields { get; set; }

        public ushort InheritanceField { get; set; }

        public bool Container { get; set; }

        public SymbolKind? Declaration { get; set; }

        public bool Declarator { get; set; }

        public TreeSitterReferenceRule? Reference { get; set; }

        public ushort TargetField { get; set; }

        public ushort ReferenceNameField { get; set; }

        public ushort ReferenceQualifierField { get; set; }

        public bool TypeIdentifier { get; set; }

        public bool QualifiedType { get; set; }

        public bool Identifier { get; set; }

        public bool QualifiedName { get; set; }

        public bool ModuleString { get; set; }

        public bool QualifiedDeclarator { get; set; }

        public bool DeclaratorWrapper { get; set; }

        public bool FunctionDeclarator { get; set; }

        public bool FileNamespace { get; set; }

        public bool Body { get; set; }

        public bool FunctionValue { get; set; }

        public bool TypeArguments { get; set; }

        public Mode Region { get; set; }
    }

    /// <summary>A grammar description resolved against the loaded language: roles by node type id, field ids by name.</summary>
    private sealed class CompiledGrammar
    {
        // Fields that hold a value, a member's name, the expression a member is read from, and an inner name.
        private static readonly string[] s_valueFields = ["value", "right", "default_value"];
        private static readonly string[] s_memberNameFields = ["name", "property", "field", "attribute"];
        private static readonly string[] s_qualifierFields = ["object", "operand", "value", "argument", "path", "scope", "module", "package"];
        private static readonly string[] s_innerFields = ["function", "type", "constructor", "macro"];

        public CompiledGrammar(TreeSitterSymbolGrammar grammar, TsLanguage language)
        {
            Grammar = grammar;
            var roles = new Dictionary<string, NodeRole>(StringComparer.Ordinal);
            NodeRole RoleOf(string type) => roles.TryGetValue(type, out var role) ? role : roles[type] = new NodeRole();
            ushort Field(string? name) => string.IsNullOrEmpty(name) ? (ushort)0 : language.GetFieldIdFromName(name);

            foreach (var (type, rule) in grammar.Definitions)
            {
                var role = RoleOf(type);
                role.Definition = rule;
                role.RequiredField = Field(rule.RequiredField);
                role.KindField = Field(rule.KindField);
                role.ContainerField = Field(rule.ContainerField);
                role.NameFields = rule.NameFields?.Select(Field).Where(id => id != 0).ToArray();
            }

            foreach (var (type, field) in grammar.InheritanceFields)
            {
                RoleOf(type).InheritanceField = Field(field);
            }

            foreach (var type in grammar.Containers)
            {
                RoleOf(type).Container = true;
            }

            foreach (var (type, kind) in grammar.Declarations)
            {
                RoleOf(type).Declaration = kind;
            }

            foreach (var (type, rule) in grammar.References)
            {
                var role = RoleOf(type);
                role.Reference = rule;
                role.TargetField = Field(rule.TargetField);
                role.ReferenceNameField = Field(rule.NameField);
                role.ReferenceQualifierField = Field(rule.QualifierField);
            }

            Flag(grammar.Declarators, role => role.Declarator = true);
            Flag(grammar.TypeIdentifiers, role => role.TypeIdentifier = true);
            Flag(grammar.QualifiedTypes, role => role.QualifiedType = true);
            Flag(grammar.Identifiers, role => role.Identifier = true);
            Flag(grammar.QualifiedNames, role => role.QualifiedName = true);
            Flag(grammar.ModuleStrings, role => role.ModuleString = true);
            Flag(grammar.QualifiedDeclarators, role => role.QualifiedDeclarator = true);
            Flag(grammar.Grammar.FileNamespaces, role => role.FileNamespace = true);
            Flag(grammar.Grammar.BodyTypes, role => role.Body = true);
            Flag(grammar.Grammar.FunctionValueTypes, role => role.FunctionValue = true);
            Flag(grammar.InheritanceRegions, role => role.Region = Mode.Inheritance);
            Flag(grammar.ImportRegions, role => role.Region = Mode.Import);
            Flag(grammar.TypeRegions, role => role.Region = Mode.TypeUse);
            Flag(grammar.TypeArgumentRegions, role =>
            {
                role.Region = Mode.TypeUse;
                role.TypeArguments = true;
            });

            // Only named node types get roles: a keyword can share its spelling with a node type ("module").
            var byId = new NodeRole?[ushort.MaxValue + 1];
            var maxId = 0;
            foreach (var symbol in language.Symbols)
            {
                if (symbol.Type != global::TreeSitter.SymbolType.Regular)
                {
                    continue;
                }

                var name = symbol.Name;
                var role = roles.GetValueOrDefault(name);
                var isWrapper = name.EndsWith("_declarator", StringComparison.Ordinal);
                if (isWrapper)
                {
                    role ??= roles[name] = new NodeRole();
                    role.DeclaratorWrapper = true;
                    role.FunctionDeclarator = name == "function_declarator";
                }

                if (role is not null)
                {
                    byId[symbol.Id] = role;
                    maxId = Math.Max(maxId, symbol.Id);
                }
            }

            Roles = byId[..(maxId + 1)];
            NameFields = [.. grammar.Grammar.NameFields.Select(Field).Where(id => id != 0)];
            BodyField = Field("body");
            DeclaratorField = Field("declarator");
            NameField = Field("name");
            LeftField = Field("left");
            ArgumentsField = Field("arguments");
            ScopeField = Field("scope");
            ValueFields = [.. s_valueFields.Select(Field).Where(id => id != 0)];
            MemberNameFields = [.. s_memberNameFields.Select(Field).Where(id => id != 0)];
            QualifierFields = [.. s_qualifierFields.Select(Field).Where(id => id != 0)];
            InnerFields = [.. s_innerFields.Select(Field).Where(id => id != 0)];

            void Flag(IEnumerable<string> types, Action<NodeRole> set)
            {
                foreach (var type in types)
                {
                    set(RoleOf(type));
                }
            }
        }

        public TreeSitterSymbolGrammar Grammar { get; }

        public NodeRole?[] Roles { get; }

        public ushort[] NameFields { get; }

        public ushort BodyField { get; }

        public ushort DeclaratorField { get; }

        public ushort NameField { get; }

        public ushort LeftField { get; }

        public ushort ArgumentsField { get; }

        public ushort ScopeField { get; }

        public ushort[] ValueFields { get; }

        public ushort[] MemberNameFields { get; }

        public ushort[] QualifierFields { get; }

        public ushort[] InnerFields { get; }

        public NodeRole? Role(TsNode node)
        {
            var id = node.TypeId;
            return id < Roles.Length ? Roles[id] : null;
        }
    }

    /// <summary>The context that applies to the descendants of the node at <see cref="Depth"/>.</summary>
    private sealed class Frame
    {
        public int Depth { get; init; }

        public string Container { get; set; } = string.Empty;

        public string From { get; set; } = string.Empty;

        public Scope Scope { get; init; }

        public string? TypeName { get; init; }

        public Mode Mode { get; init; }

        public bool SuppressDefinitions { get; init; }
    }

    /// <summary>One walk over one tree.</summary>
    private sealed class Walker(CompiledGrammar compiled, SymbolSink sink)
    {
        private const int MaxNameLength = 200;

        private readonly TreeSitterSymbolGrammar _grammar = compiled.Grammar;
        private readonly string _text = sink.Document.Text;
        private readonly Stack<Frame> _frames = new();
        private readonly HashSet<(int Start, int End, ushort Type)> _suppressed = [];
        private readonly Dictionary<(int Start, int End, ushort Type), Mode> _pending = [];

        public void Run(TsNode root)
        {
            DefineFileModule();
            _frames.Push(new Frame { Depth = -1 });
            using var cursor = new TsTreeCursor(root);
            var depth = 0;
            while (true)
            {
                if (Enter(cursor.CurrentNode, depth) && cursor.GotoFirstChild())
                {
                    depth++;
                    continue;
                }

                while (!cursor.GotoNextSibling())
                {
                    if (!cursor.GotoParent())
                    {
                        return;
                    }

                    depth--;
                }

                while (_frames.Peek().Depth >= depth)
                {
                    _frames.Pop();
                }
            }
        }

        /// <summary>Handles a node; returns whether to walk its children.</summary>
        private bool Enter(TsNode node, int depth)
        {
            var role = compiled.Role(node);
            if (role is null)
            {
                return true;
            }

            if (_suppressed.Count > 0 && _suppressed.Remove(Key(node)))
            {
                return false;
            }

            var frame = _frames.Peek();
            var mode = frame.Mode;
            if (_pending.Count > 0 && _pending.Remove(Key(node), out var pendingMode))
            {
                mode = pendingMode;
                frame = Push(depth, frame, frame.Container, frame.From, frame.Scope, frame.TypeName, mode, frame.SuppressDefinitions);
            }

            if (role.FileNamespace)
            {
                DefineFileNamespace(node, frame);
                return false;
            }

            if (role.Definition is not null && DefineNode(node, role, frame, depth))
            {
                return true;
            }

            if (role.Container)
            {
                OpenContainer(node, role, frame, depth);
                return true;
            }

            if (role.Declaration is { } declaration && Declare(node, declaration, frame, depth))
            {
                return true;
            }

            if (role.Reference is not null)
            {
                Reference(node, role, frame);
                return true;
            }

            if (role.Region != Mode.Normal)
            {
                Push(depth, frame, frame.Container, frame.From, frame.Scope, frame.TypeName, role.Region, frame.SuppressDefinitions);
                return true;
            }

            if (role.TypeIdentifier)
            {
                Emit(Text(node), mode == Mode.Normal ? SymbolReferenceKind.TypeUse : KindOf(mode), node.StartIndex, null, frame);
                return false;
            }

            if (role.QualifiedType)
            {
                var (name, qualifier, leaf) = NameOf(node);
                Emit(name, mode == Mode.Normal ? SymbolReferenceKind.TypeUse : KindOf(mode), leaf?.StartIndex ?? node.StartIndex, qualifier, frame);
                return false;
            }

            if (mode != Mode.Normal)
            {
                if (role.Identifier)
                {
                    Emit(Text(node), KindOf(mode), node.StartIndex, null, frame);
                    return false;
                }

                if (role.QualifiedName)
                {
                    var (name, qualifier, leaf) = NameOf(node);
                    Emit(name, KindOf(mode), leaf?.StartIndex ?? node.StartIndex, qualifier, frame);
                    return false;
                }

                if (role.ModuleString && mode == Mode.Import)
                {
                    Emit(ModuleName(Text(node)), SymbolReferenceKind.Import, node.StartIndex, null, frame);
                    return false;
                }
            }

            return true;
        }

        private bool DefineNode(TsNode node, NodeRole role, Frame frame, int depth)
        {
            var rule = role.Definition!;
            if (role.RequiredField != 0 && node.GetChildForField(role.RequiredField) is null)
            {
                return false;
            }

            if (frame.Scope == Scope.Function || frame.SuppressDefinitions)
            {
                return false;
            }

            var (name, leaf, scope) = DefinitionName(node, role.NameFields ?? compiled.NameFields);
            var kind = rule.Kind;
            if (role.KindField != 0 && node.GetChildForField(role.KindField) is { } kindNode
                && rule.KindByFieldType.TryGetValue(kindNode.Type, out var refined))
            {
                kind = refined;
            }

            var container = frame.Container;
            if (role.ContainerField != 0 && node.GetChildForField(role.ContainerField) is { } owner && FirstTypeName(owner) is { Length: > 0 } ownerName)
            {
                container = Join(container, ownerName);
            }

            if (scope is { Length: > 0 })
            {
                container = Join(container, scope);
                kind = kind == SymbolKind.Function ? SymbolKind.Method : kind;
            }

            if (kind == SymbolKind.Function && frame.Scope == Scope.Type)
            {
                kind = SymbolKind.Method;
            }

            if (kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Variable && IsFunctionValued(node))
            {
                kind = frame.Scope == Scope.Type ? SymbolKind.Method : SymbolKind.Function;
            }

            if (kind is SymbolKind.Function or SymbolKind.Method
                && (_grammar.ConstructorNames.Contains(name) || (name.Length > 0 && name == frame.TypeName)))
            {
                kind = SymbolKind.Constructor;
            }

            var isContainer = kind is SymbolKind.Namespace or SymbolKind.Module or SymbolKind.Class or SymbolKind.Struct
                or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Type;
            if (!IsName(name))
            {
                // A type without a name (an anonymous C struct) hides what is inside it.
                Push(depth, frame, container, frame.From, isContainer ? Scope.Type : Scope.Function, frame.TypeName, frame.Mode,
                    suppressDefinitions: true);
                return true;
            }

            sink.Define(name, kind, container, node.StartIndex, node.EndIndex, Signature(node.StartIndex, HeaderEnd(node)));
            if (leaf is { } nameNode)
            {
                _suppressed.Add(Key(nameNode));
            }

            MarkInheritance(node, role);
            var childScope = kind switch
            {
                SymbolKind.Namespace or SymbolKind.Module or SymbolKind.TypeAlias or SymbolKind.Delegate => Scope.None,
                _ when isContainer => Scope.Type,
                _ => Scope.Function,
            };

            Push(depth, frame, isContainer ? Join(container, name) : container, Join(container, name), childScope,
                isContainer ? name : frame.TypeName, frame.Mode, frame.SuppressDefinitions);
            return true;
        }

        private void OpenContainer(TsNode node, NodeRole role, Frame frame, int depth)
        {
            var name = string.Empty;
            foreach (var field in compiled.NameFields)
            {
                if (node.GetChildForField(field) is { } named)
                {
                    name = NameOf(named).Name;
                    break;
                }
            }

            MarkInheritance(node, role);
            var container = IsName(name) ? Join(frame.Container, name) : frame.Container;
            Push(depth, frame, container, container, Scope.Type, IsName(name) ? name : frame.TypeName, frame.Mode, frame.SuppressDefinitions);
        }

        private bool Declare(TsNode node, SymbolKind declarationKind, Frame frame, int depth)
        {
            if (frame.Scope == Scope.Function || frame.SuppressDefinitions)
            {
                return false;
            }

            var declarators = new List<TsNode>();
            using (var cursor = new TsTreeCursor(node))
            {
                if (cursor.GotoFirstChild())
                {
                    do
                    {
                        var child = cursor.CurrentNode;
                        if (child.IsNamed && (compiled.Role(child)?.Declarator == true
                            || (compiled.DeclaratorField != 0 && cursor.CurrentFieldId == compiled.DeclaratorField)))
                        {
                            declarators.Add(child);
                        }
                    }
                    while (cursor.GotoNextSibling());
                }
            }

            if (declarators.Count == 0)
            {
                declarators.Add(node);
            }

            string? first = null;
            var nodeLine = sink.Document.LineOfOffset(node.StartIndex);
            foreach (var declarator in declarators)
            {
                var nameNode = declarator == node
                    ? Child(node, compiled.LeftField) ?? Child(node, compiled.NameField)
                    : Child(declarator, compiled.NameField) ?? Child(declarator, compiled.DeclaratorField) ?? declarator;
                if (nameNode is null)
                {
                    continue;
                }

                var (leaf, isFunction, _) = FollowDeclarator(nameNode);
                if (leaf is not { } nameLeaf || compiled.Role(nameLeaf) is not { } leafRole || !(leafRole.Identifier || leafRole.TypeIdentifier))
                {
                    continue;
                }

                var name = Text(nameLeaf);
                if (!IsName(name))
                {
                    continue;
                }

                var kind = declarationKind;
                var value = Value(declarator);
                var functionValue = value is not null && compiled.Role(value)?.FunctionValue == true;
                if (isFunction || functionValue)
                {
                    kind = SymbolKind.Function;
                }

                if (frame.Scope == Scope.Type)
                {
                    kind = kind switch
                    {
                        SymbolKind.Function => SymbolKind.Method,
                        SymbolKind.Variable => SymbolKind.Field,
                        _ => kind,
                    };
                }

                var headerStart = declarator == declarators[0] && sink.Document.LineOfOffset(declarator.StartIndex) == nodeLine
                    ? node.StartIndex
                    : declarator.StartIndex;
                var headerEnd = functionValue ? HeaderEnd(value!) : value?.StartIndex ?? FirstLineEnd(declarator);
                sink.Define(name, kind, frame.Container, declarator.StartIndex, declarator.EndIndex, Signature(headerStart, headerEnd));
                _suppressed.Add(Key(nameLeaf));
                first ??= name;
            }

            if (first is null)
            {
                return false;
            }

            Push(depth, frame, frame.Container, Join(frame.Container, first), Scope.Function, frame.TypeName, frame.Mode, frame.SuppressDefinitions);
            return true;
        }

        private void Reference(TsNode node, NodeRole role, Frame frame)
        {
            var rule = role.Reference!;
            string name;
            string? qualifier;
            TsNode? leaf;
            if (role.TargetField != 0)
            {
                if (node.GetChildForField(role.TargetField) is not { } target || compiled.Role(target)?.Reference is not null)
                {
                    return;
                }

                (name, qualifier, leaf) = NameOf(target);
            }
            else if (role.ReferenceNameField != 0)
            {
                if (node.GetChildForField(role.ReferenceNameField) is not { } nameNode)
                {
                    return;
                }

                (name, _, leaf) = NameOf(nameNode);
                qualifier = role.ReferenceQualifierField != 0 && node.GetChildForField(role.ReferenceQualifierField) is { } qualifierNode
                    ? NameOf(qualifierNode).Name
                    : null;
            }
            else
            {
                if (FirstNamedChild(node) is not { } target || compiled.Role(target)?.Reference is not null)
                {
                    return;
                }

                (name, qualifier, leaf) = NameOf(target);
            }

            if (!Emit(name, rule.Kind, leaf?.StartIndex ?? node.StartIndex, qualifier, frame))
            {
                return;
            }

            if (leaf is { } referenced)
            {
                _suppressed.Add(Key(referenced));
            }

            if (rule.Kind == SymbolReferenceKind.Call && _grammar.RequireFunctions.Contains(name)
                && Child(node, compiled.ArgumentsField) is { } arguments && FirstNamedChild(arguments) is { } argument
                && compiled.Role(argument)?.ModuleString == true)
            {
                Emit(ModuleName(Text(argument)), SymbolReferenceKind.Import, argument.StartIndex, null, frame);
            }
        }

        private void DefineFileNamespace(TsNode node, Frame frame)
        {
            var name = FirstNamedChild(node) is { } child ? Text(child) : string.Empty;
            if (!IsName(name))
            {
                return;
            }

            var line = sink.Document.LineOfOffset(node.StartIndex);
            sink.DefineLines(name, SymbolKind.Namespace, frame.Container, line, sink.Document.LineCount,
                Signature(node.StartIndex, FirstLineEnd(node)));
            frame.Container = Join(frame.Container, name);
            frame.From = frame.Container;
        }

        private void DefineFileModule()
        {
            if (_grammar.FileModule == FileModuleNaming.None)
            {
                return;
            }

            var path = sink.Document.Path;
            var fileName = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            var name = fileName;
            if (_grammar.FileModule == FileModuleNaming.Stem)
            {
                name = Path.GetFileNameWithoutExtension(fileName);
                if (name is "index" or "__init__" or "mod" or "lib")
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                    name = string.IsNullOrEmpty(folder) ? name : folder;
                }
            }

            if (name.Length > 0)
            {
                sink.DefineLines(name, SymbolKind.Module, string.Empty, 1, sink.Document.LineCount, "module " + name);
            }
        }

        private void MarkInheritance(TsNode node, NodeRole role)
        {
            if (role.InheritanceField != 0 && node.GetChildForField(role.InheritanceField) is { } bases)
            {
                _pending[Key(bases)] = Mode.Inheritance;
            }
        }

        private bool Emit(string name, SymbolReferenceKind kind, int offset, string? qualifier, Frame frame)
        {
            if (!IsName(name) || _grammar.IgnoredNames.Contains(name))
            {
                return false;
            }

            if (qualifier is not null && (!IsName(qualifier) || _grammar.IgnoredNames.Contains(qualifier)))
            {
                qualifier = null;
            }

            sink.Reference(name, kind, offset, qualifier, frame.From);
            return true;
        }

        /// <summary>The rightmost simple name of an expression or type, the name before it, and the leaf it came from.</summary>
        private (string Name, string? Qualifier, TsNode? Leaf) NameOf(TsNode node, int depth = 0)
        {
            if (depth > 8 || node.IsMissing)
            {
                return (string.Empty, null, null);
            }

            var role = compiled.Role(node);
            if (role is { Identifier: true } or { TypeIdentifier: true })
            {
                return (Text(node), null, node);
            }

            foreach (var field in compiled.MemberNameFields)
            {
                if (node.GetChildForField(field) is { } member)
                {
                    var (name, _, leaf) = NameOf(member, depth + 1);
                    string? qualifier = null;
                    foreach (var qualifierField in compiled.QualifierFields)
                    {
                        if (node.GetChildForField(qualifierField) is { } owner)
                        {
                            qualifier = NameOf(owner, depth + 1).Name;
                            break;
                        }
                    }

                    return (name, qualifier, leaf);
                }
            }

            foreach (var field in compiled.InnerFields)
            {
                if (node.GetChildForField(field) is { } inner)
                {
                    return NameOf(inner, depth + 1);
                }
            }

            TsNode? last = null;
            TsNode? previous = null;
            using (var cursor = new TsTreeCursor(node))
            {
                if (cursor.GotoFirstChild())
                {
                    do
                    {
                        var child = cursor.CurrentNode;
                        if (child.IsNamed && compiled.Role(child) is not { TypeArguments: true } && cursor.CurrentFieldId != compiled.ArgumentsField)
                        {
                            previous = last;
                            last = child;
                        }
                    }
                    while (cursor.GotoNextSibling());
                }
            }

            if (last is null)
            {
                return node.NamedChildren.Count == 0 ? (Text(node), null, node) : (string.Empty, null, null);
            }

            var (lastName, _, lastLeaf) = NameOf(last, depth + 1);
            return (lastName, previous is null ? null : NameOf(previous, depth + 1).Name, lastLeaf);
        }

        /// <summary>A definition's name: the first name field present, followed through C declarators.</summary>
        private (string Name, TsNode? Leaf, string? Scope) DefinitionName(TsNode node, ushort[] fields)
        {
            foreach (var field in fields)
            {
                if (node.GetChildForField(field) is { } child)
                {
                    var (leaf, _, scope) = FollowDeclarator(child);
                    return leaf is null ? (string.Empty, null, null) : (Text(leaf), leaf, scope);
                }
            }

            // As the chunker does: a child with a name field (a Go type declaration's spec).
            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    if (cursor.CurrentNode.IsNamed && Child(cursor.CurrentNode, compiled.NameField) is { } named)
                    {
                        var (leaf, _, scope) = FollowDeclarator(named);
                        return leaf is null ? (string.Empty, null, null) : (Text(leaf), leaf, scope);
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return (string.Empty, null, null);
        }

        /// <summary>
        /// Follows C and C++ declarators (pointer, reference, function, array) down to the name; reports whether a
        /// function declarator was passed and the scope of a qualified name (<c>Matrix</c> for <c>Matrix&lt;T&gt;::rows</c>).
        /// </summary>
        private (TsNode? Leaf, bool IsFunction, string? Scope) FollowDeclarator(TsNode node)
        {
            var isFunction = false;
            string? scope = null;
            for (var guard = 0; guard < 32; guard++)
            {
                var role = compiled.Role(node);
                if (role is null)
                {
                    break;
                }

                if (role.QualifiedDeclarator)
                {
                    if (Child(node, compiled.ScopeField) is { } scopeNode)
                    {
                        scope = NameOf(scopeNode).Name;
                    }

                    if (Child(node, compiled.NameField) is not { } inner)
                    {
                        break;
                    }

                    node = inner;
                    continue;
                }

                if (!role.DeclaratorWrapper)
                {
                    break;
                }

                isFunction |= role.FunctionDeclarator;
                var next = Child(node, compiled.DeclaratorField) ?? FirstNamedChild(node);
                if (next is null)
                {
                    break;
                }

                node = next;
            }

            var leafRole = compiled.Role(node);
            var leaf = leafRole is { Identifier: true } or { TypeIdentifier: true } ? node : NameOf(node).Leaf;
            return (leaf, isFunction, scope);
        }

        /// <summary>The first type name inside a node (a Go receiver's type).</summary>
        private string? FirstTypeName(TsNode node)
        {
            using var cursor = new TsTreeCursor(node);
            var depth = 0;
            while (depth < 16)
            {
                var current = cursor.CurrentNode;
                if (compiled.Role(current) is { TypeIdentifier: true })
                {
                    return Text(current);
                }

                if (cursor.GotoFirstChild())
                {
                    depth++;
                    continue;
                }

                while (!cursor.GotoNextSibling())
                {
                    if (depth == 0 || !cursor.GotoParent())
                    {
                        return null;
                    }

                    depth--;
                }

                if (depth == 0)
                {
                    return null;
                }
            }

            return null;
        }

        private bool IsFunctionValued(TsNode node) => Value(node) is { } value && compiled.Role(value)?.FunctionValue == true;

        private TsNode? Value(TsNode node)
        {
            foreach (var field in compiled.ValueFields)
            {
                if (node.GetChildForField(field) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>Where a declaration's header ends: the start of its body, else the end of its first line.</summary>
        private int HeaderEnd(TsNode node)
        {
            var body = Child(node, compiled.BodyField);
            if (body is null)
            {
                using var cursor = new TsTreeCursor(node);
                if (cursor.GotoFirstChild())
                {
                    do
                    {
                        var child = cursor.CurrentNode;
                        if (child.IsNamed && compiled.Role(child) is { Body: true })
                        {
                            body = child;
                            break;
                        }
                    }
                    while (cursor.GotoNextSibling());
                }
            }

            return body?.StartIndex ?? FirstLineEnd(node);
        }

        private int FirstLineEnd(TsNode node)
        {
            var start = Math.Clamp(node.StartIndex, 0, _text.Length);
            var end = Math.Clamp(node.EndIndex, start, _text.Length);
            var lineBreak = _text.AsSpan(start, end - start).IndexOfAny('\r', '\n');
            return lineBreak < 0 ? end : start + lineBreak;
        }

        private string Signature(int start, int end)
        {
            start = Math.Clamp(start, 0, _text.Length);
            end = Math.Clamp(end, start, Math.Min(_text.Length, start + (SignatureText.MaxLength * 4)));
            return SignatureText.Normalize(_text.AsSpan(start, end - start));
        }

        private string Text(TsNode node)
        {
            var start = Math.Clamp(node.StartIndex, 0, _text.Length);
            var end = Math.Clamp(node.EndIndex, start, Math.Min(_text.Length, start + MaxNameLength));
            return sink.Share(_text[start..end]);
        }

        /// <summary>The module an import string names: the last path segment, without its extension unless modules are named by file name.</summary>
        private string ModuleName(string literal)
        {
            var value = literal.Trim().Trim('"', '\'', '`', '<', '>').Trim();
            var name = value[(value.LastIndexOfAny(['/', '\\']) + 1)..];
            if (_grammar.FileModule != FileModuleNaming.FileName)
            {
                var dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    name = name[..dot];
                }
            }

            return name;
        }

        private Frame Push(int depth, Frame parent, string container, string from, Scope scope, string? typeName, Mode mode, bool suppressDefinitions)
        {
            var frame = new Frame
            {
                Depth = depth,
                Container = container,
                From = from,
                Scope = scope,
                TypeName = typeName,
                Mode = mode,
                SuppressDefinitions = suppressDefinitions || parent.SuppressDefinitions,
            };
            _frames.Push(frame);
            return frame;
        }

        private string Join(string container, string name)
        {
            var segment = Breadcrumbs.Sanitize(name);
            if (segment.Length == 0)
            {
                return container;
            }

            return sink.Share(container.Length == 0 ? segment : container + Breadcrumbs.Separator + segment);
        }

        private static (int Start, int End, ushort Type) Key(TsNode node) => (node.StartIndex, node.EndIndex, node.TypeId);

        private static TsNode? Child(TsNode node, ushort field) => field == 0 ? null : node.GetChildForField(field);

        private static TsNode? FirstNamedChild(TsNode node)
        {
            using var cursor = new TsTreeCursor(node);
            if (cursor.GotoFirstChild())
            {
                do
                {
                    if (cursor.CurrentNode.IsNamed)
                    {
                        return cursor.CurrentNode;
                    }
                }
                while (cursor.GotoNextSibling());
            }

            return null;
        }

        private static SymbolReferenceKind KindOf(Mode mode) => mode switch
        {
            Mode.Inheritance => SymbolReferenceKind.Inheritance,
            Mode.Import => SymbolReferenceKind.Import,
            _ => SymbolReferenceKind.TypeUse,
        };

        /// <summary>True for text that looks like an identifier (letters, digits, <c>_</c>, <c>$</c>; not starting with a digit).</summary>
        private static bool IsName(string? name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] is '_' or '$'))
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c is '_' or '$' or '.' or '-'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
