using System.Collections.Frozen;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// How to chunk one tree-sitter grammar, as data: which node types are namespaces, types and members, which wrap a
/// declaration, which are skipped as leading material, and where statements start inside a body. The generic
/// <see cref="TreeSitterChunker"/> walks any grammar described this way, so adding a language means adding a record
/// to <see cref="TreeSitterGrammars"/> (and its native library to the project file), not code.
/// </summary>
/// <remarks>Node type names are the grammar's own (see its <c>node-types.json</c>).</remarks>
public sealed record TreeSitterGrammar
{
    /// <summary>The language recorded on chunks (one of <see cref="LanguageIds"/> or a new identifier).</summary>
    public required string Language { get; init; }

    /// <summary>
    /// The grammar identifier the bindings load: the native library is <c>tree-sitter-&lt;id&gt;</c> lower-cased
    /// (<c>C++</c> maps to <c>cpp</c>) and its entry point <c>tree_sitter_&lt;id&gt;</c>.
    /// </summary>
    public required string GrammarId { get; init; }

    /// <summary>Containers whose name joins the breadcrumb and whose body is walked (C++ namespaces, Rust modules).</summary>
    public IReadOnlySet<string> Namespaces { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Types: a header chunk plus the members of the body, or one chunk for the whole type when the body has no
    /// members (an enum, a struct of fields).
    /// </summary>
    public IReadOnlySet<string> Types { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Members: one chunk each.</summary>
    public IReadOnlySet<string> Members { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Members that are grouped with their neighbours of the same kind (fields, properties, constants).</summary>
    public IReadOnlySet<string> FieldMembers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Nodes that wrap one declaration (export statements, decorators, templates): the declaration is classified, the
    /// wrapper's range is used. The declaration is the <c>declaration</c> or <c>definition</c> field, else the last
    /// named child.
    /// </summary>
    public IReadOnlySet<string> Wrappers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Containers walked as if their children were at the same level (<c>#ifdef</c>, <c>extern "C"</c>).</summary>
    public IReadOnlySet<string> TransparentContainers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Nodes that never form a chunk of their own and lead into the next one: comments, and attributes or decorators
    /// that the grammar places beside the declaration rather than inside it.
    /// </summary>
    public IReadOnlySet<string> Leading { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Clauses that name the file's package for everything after them (Go <c>package</c>, Java <c>package</c>).</summary>
    public IReadOnlySet<string> FileNamespaces { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Statement containers: their named children are where a member over the cap may be split.</summary>
    public IReadOnlySet<string> Blocks { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Node types accepted as a type's or namespace's body when it has no <c>body</c> field.</summary>
    public IReadOnlySet<string> BodyTypes { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Variable declarations that count as members when a declarator's value is one of
    /// <see cref="FunctionValueTypes"/> (<c>const handler = () =&gt; {}</c>).
    /// </summary>
    public IReadOnlySet<string> FunctionValueDeclarations { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Function expression node types for <see cref="FunctionValueDeclarations"/>.</summary>
    public IReadOnlySet<string> FunctionValueTypes { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Fields tried in order for a node's name; a <c>declarator</c> is followed down to the identifier.</summary>
    public IReadOnlyList<string> NameFields { get; init; } = ["name"];

    /// <summary>
    /// The share of the text inside error nodes above which the parse counts as failed and the file is chunked by
    /// windows instead.
    /// </summary>
    public double MaxErrorFraction { get; init; } = 0.3;

    /// <summary>Builds a frozen set of node type names.</summary>
    /// <param name="types">The names.</param>
    /// <returns>The set.</returns>
    public static IReadOnlySet<string> Set(params string[] types) => types.ToFrozenSet(StringComparer.Ordinal);
}
