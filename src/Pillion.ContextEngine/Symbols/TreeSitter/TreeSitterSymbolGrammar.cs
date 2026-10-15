using System.Collections.Frozen;
using Pillion.ContextEngine.Chunking.TreeSitter;

namespace Pillion.ContextEngine.Symbols.TreeSitter;

/// <summary>
/// How to read symbols from one tree-sitter grammar, as data, in the same spirit as <see cref="TreeSitterGrammar"/>
/// for chunking: which node types define what, which open a container, which carry references, and which regions of
/// the tree make the names inside them inheritance, imports or type uses. The generic
/// <see cref="TreeSitterSymbolExtractor"/> walks any grammar described this way, so adding a language means adding a
/// record to <see cref="TreeSitterSymbolGrammars"/>, not code.
/// </summary>
/// <remarks>
/// Node type and field names are the grammar's own (see its <c>node-types.json</c>). Definitions are never taken from
/// inside a function, method or variable initializer (locals are not symbols), and a type without a name hides the
/// definitions inside it (the fields of an anonymous C struct are not file-level symbols).
/// </remarks>
public sealed record TreeSitterSymbolGrammar
{
    /// <summary>The chunking description of the same grammar: language, native grammar, error tolerance, name fields and file namespaces.</summary>
    public required TreeSitterGrammar Grammar { get; init; }

    /// <summary>Node types that define a symbol, with the kind and any extra rules.</summary>
    public IReadOnlyDictionary<string, TreeSitterDefinitionRule> Definitions { get; init; } = FrozenDictionary<string, TreeSitterDefinitionRule>.Empty;

    /// <summary>
    /// Node types that open a container without defining a symbol, named by their <c>type</c> field or
    /// <see cref="TreeSitterGrammar.NameFields"/> (Rust <c>impl</c> blocks: the methods inside belong to the type).
    /// </summary>
    public IReadOnlySet<string> Containers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Declarations whose declarators define names at file or type scope: variables, constants and fields (JavaScript
    /// <c>lexical_declaration</c>, Go <c>const_declaration</c>, Java <c>field_declaration</c>, C <c>declaration</c>). A
    /// declarator whose value is a function (<see cref="TreeSitterGrammar.FunctionValueTypes"/>) or whose declarator
    /// chain holds a <c>function_declarator</c> (a C prototype) defines a function instead; a variable inside a type is a
    /// field.
    /// </summary>
    public IReadOnlyDictionary<string, SymbolKind> Declarations { get; init; } = FrozenDictionary<string, SymbolKind>.Empty;

    /// <summary>
    /// Node types inside a declaration that each carry one name (<c>variable_declarator</c>, <c>const_spec</c>). Children
    /// in a <c>declarator</c> field count too; a declaration with neither is its own declarator (a Python assignment,
    /// named by its <c>left</c> field).
    /// </summary>
    public IReadOnlySet<string> Declarators { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Node types that are references, with the kind and where the referenced name is (calls, constructions, annotations).</summary>
    public IReadOnlyDictionary<string, TreeSitterReferenceRule> References { get; init; } = FrozenDictionary<string, TreeSitterReferenceRule>.Empty;

    /// <summary>Leaf nodes that always name a type (<c>type_identifier</c>): a type use wherever they appear, except as a definition's own name.</summary>
    public IReadOnlySet<string> TypeIdentifiers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Composite nodes that always name a type and are read as one reference (<c>scoped_type_identifier</c>, Go <c>qualified_type</c>).</summary>
    public IReadOnlySet<string> QualifiedTypes { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Leaf identifier nodes; inside an inheritance, import or type region they are references.</summary>
    public IReadOnlySet<string> Identifiers { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Composite names read as one reference inside a region (<c>member_expression</c>, <c>scoped_identifier</c>, Python <c>dotted_name</c>).</summary>
    public IReadOnlySet<string> QualifiedNames { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Regions whose names are base types (<c>class_heritage</c>, <c>superclass</c>, <c>base_class_clause</c>).</summary>
    public IReadOnlySet<string> InheritanceRegions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Definition or container node types whose field holds base types (Python <c>class_definition</c> → <c>superclasses</c>, Rust <c>impl_item</c> → <c>trait</c>).</summary>
    public IReadOnlyDictionary<string, string> InheritanceFields { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>Regions whose names are imports (<c>import_statement</c>, <c>use_declaration</c>, <c>preproc_include</c>).</summary>
    public IReadOnlySet<string> ImportRegions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Regions whose identifiers are type uses (Python's <c>type</c> annotations).</summary>
    public IReadOnlySet<string> TypeRegions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Type argument lists: names inside are type uses even within an inheritance region.</summary>
    public IReadOnlySet<string> TypeArgumentRegions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>String literal node types that name a module inside an import region (<c>"./logger"</c>, <c>"net/http"</c>, <c>&lt;stdio.h&gt;</c>).</summary>
    public IReadOnlySet<string> ModuleStrings { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Declarator node types that hold a qualified name (C++ <c>Matrix&lt;T&gt;::rows</c>): the scope joins the container.</summary>
    public IReadOnlySet<string> QualifiedDeclarators { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Whether each file is also a module symbol, and how the module is named.</summary>
    public FileModuleNaming FileModule { get; init; } = FileModuleNaming.None;

    /// <summary>Method names that are constructors (<c>constructor</c>, <c>__init__</c>); a method named after its type is one too.</summary>
    public IReadOnlySet<string> ConstructorNames { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Names that are never references or qualifiers (<c>this</c>, <c>self</c>, <c>Self</c>).</summary>
    public IReadOnlySet<string> IgnoredNames { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Functions that import the module named by their first string argument (<c>require("express")</c>).</summary>
    public IReadOnlySet<string> RequireFunctions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The language recorded on symbols.</summary>
    public string Language => Grammar.Language;
}

/// <summary>How a node type defines a symbol.</summary>
/// <param name="Kind">The kind. <see cref="SymbolKind.Function"/> becomes <see cref="SymbolKind.Method"/> inside a type.</param>
public sealed record TreeSitterDefinitionRule(SymbolKind Kind)
{
    /// <summary>A field the node must have to be a definition (C <c>struct_specifier</c> needs a <c>body</c>; without one it is a type use).</summary>
    public string? RequiredField { get; init; }

    /// <summary>A field whose node type refines the kind (Go <c>type_spec</c>: the <c>type</c> field).</summary>
    public string? KindField { get; init; }

    /// <summary>Refined kinds by the node type of <see cref="KindField"/> (<c>struct_type</c> → <see cref="SymbolKind.Struct"/>).</summary>
    public IReadOnlyDictionary<string, SymbolKind> KindByFieldType { get; init; } = FrozenDictionary<string, SymbolKind>.Empty;

    /// <summary>A field whose first type name joins the container (Go <c>method_declaration</c>: the <c>receiver</c>).</summary>
    public string? ContainerField { get; init; }

    /// <summary>Fields tried for the name; <see cref="TreeSitterGrammar.NameFields"/> when null.</summary>
    public IReadOnlyList<string>? NameFields { get; init; }
}

/// <summary>Where the referenced name of a reference node is.</summary>
/// <param name="Kind">The reference kind.</param>
/// <param name="TargetField">The field holding the referenced expression (<c>function</c> of a call); its rightmost name is the reference. Null with <paramref name="NameField"/> null: the first named child.</param>
/// <param name="NameField">With no target field: the field holding the name on the node itself (Java <c>method_invocation</c> → <c>name</c>).</param>
/// <param name="QualifierField">With <paramref name="NameField"/>: the field holding the qualifier (<c>object</c>).</param>
public sealed record TreeSitterReferenceRule(SymbolReferenceKind Kind, string? TargetField = null, string? NameField = null, string? QualifierField = null);

/// <summary>Whether each file of a language is a module symbol, and its name.</summary>
public enum FileModuleNaming
{
    /// <summary>No module symbol.</summary>
    None,

    /// <summary>A module named by the file name without its extension (<c>inventory</c>); <c>index</c>, <c>__init__</c>, <c>mod</c> and <c>lib</c> take the folder's name.</summary>
    Stem,

    /// <summary>A module named by the file name with its extension (<c>list.h</c>), matching <c>#include</c>.</summary>
    FileName,
}
