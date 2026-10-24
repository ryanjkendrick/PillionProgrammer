using System.Collections.Frozen;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// What a definition declares. The numeric values are stable (they may be persisted); <see cref="SymbolKinds.Name(SymbolKind)"/>
/// gives the stable lower-case name used in the <c>symbols.kind</c> column and in the repo map.
/// </summary>
public enum SymbolKind
{
    /// <summary>A namespace or package (C# and VB namespaces, C++ namespaces, Go and Java packages).</summary>
    Namespace = 1,

    /// <summary>A module: a VB module, a Rust <c>mod</c>, or a file that is a module (Python, JavaScript, TypeScript, C and C++).</summary>
    Module = 2,

    /// <summary>A class (including records and Razor components).</summary>
    Class = 3,

    /// <summary>A struct or VB structure.</summary>
    Struct = 4,

    /// <summary>An interface, a Rust trait or a Java annotation type.</summary>
    Interface = 5,

    /// <summary>An enumeration.</summary>
    Enum = 6,

    /// <summary>A member of an enumeration.</summary>
    EnumMember = 7,

    /// <summary>A delegate type.</summary>
    Delegate = 8,

    /// <summary>A type alias (<c>typedef</c>, TypeScript <c>type</c>, Rust <c>type</c>, C++ <c>using</c>).</summary>
    TypeAlias = 9,

    /// <summary>Any other named type (a Go type that is neither a struct nor an interface, a C or Rust union).</summary>
    Type = 10,

    /// <summary>A free function (or a function-valued variable such as <c>const f = () =&gt; {}</c>).</summary>
    Function = 11,

    /// <summary>A method: a function declared inside a type.</summary>
    Method = 12,

    /// <summary>A constructor. Constructors are not found by name (they share their type's name; see <see cref="SymbolGraph"/>).</summary>
    Constructor = 13,

    /// <summary>A property, indexer or interface property signature.</summary>
    Property = 14,

    /// <summary>A field.</summary>
    Field = 15,

    /// <summary>A constant.</summary>
    Constant = 16,

    /// <summary>A variable at file or module scope (never a local).</summary>
    Variable = 17,

    /// <summary>An event.</summary>
    Event = 18,

    /// <summary>A function-like macro (C and C++ <c>#define f(x)</c>, Rust <c>macro_rules!</c>).</summary>
    Macro = 19,

    /// <summary>An SQL table.</summary>
    Table = 20,

    /// <summary>An SQL view.</summary>
    View = 21,

    /// <summary>An SQL stored procedure.</summary>
    Procedure = 22,

    /// <summary>A CSS class selector, named with its dot (<c>.card</c>).</summary>
    Selector = 23,

    /// <summary>An HTML element id, named with its hash (<c>#top</c>).</summary>
    ElementId = 24,
}

/// <summary>How a reference uses the name it refers to. The numeric values are stable (they may be persisted).</summary>
public enum SymbolReferenceKind
{
    /// <summary>A call, an object creation (<c>new Foo()</c>), a decorator or a macro invocation.</summary>
    Call = 1,

    /// <summary>A name in a type position: a declaration's type, a type argument, a cast, an attribute or annotation.</summary>
    TypeUse = 2,

    /// <summary>A base class or implemented interface (<c>extends</c>, <c>implements</c>, a base list, a Rust trait impl).</summary>
    Inheritance = 3,

    /// <summary>An import: <c>using</c>, <c>Imports</c>, <c>import</c>, <c>use</c>, <c>#include</c>.</summary>
    Import = 4,

    /// <summary>
    /// Any other use of a name: a member access that is not a call (<c>order.Total</c>), a field or property read,
    /// an SQL table named in <c>FROM</c> or <c>JOIN</c>, an HTML class that a CSS rule defines. Only C#, VB, SQL, HTML
    /// and CSS produce these.
    /// </summary>
    Use = 5,
}

/// <summary>Stable names and groupings for <see cref="SymbolKind"/> and <see cref="SymbolReferenceKind"/>.</summary>
public static class SymbolKinds
{
    private static readonly FrozenDictionary<SymbolKind, string> s_names = new Dictionary<SymbolKind, string>
    {
        [SymbolKind.Namespace] = "namespace",
        [SymbolKind.Module] = "module",
        [SymbolKind.Class] = "class",
        [SymbolKind.Struct] = "struct",
        [SymbolKind.Interface] = "interface",
        [SymbolKind.Enum] = "enum",
        [SymbolKind.EnumMember] = "enum-member",
        [SymbolKind.Delegate] = "delegate",
        [SymbolKind.TypeAlias] = "type-alias",
        [SymbolKind.Type] = "type",
        [SymbolKind.Function] = "function",
        [SymbolKind.Method] = "method",
        [SymbolKind.Constructor] = "constructor",
        [SymbolKind.Property] = "property",
        [SymbolKind.Field] = "field",
        [SymbolKind.Constant] = "const",
        [SymbolKind.Variable] = "variable",
        [SymbolKind.Event] = "event",
        [SymbolKind.Macro] = "macro",
        [SymbolKind.Table] = "table",
        [SymbolKind.View] = "view",
        [SymbolKind.Procedure] = "procedure",
        [SymbolKind.Selector] = "selector",
        [SymbolKind.ElementId] = "element-id",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, SymbolKind> s_byName =
        s_names.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static readonly FrozenDictionary<SymbolReferenceKind, string> s_referenceNames = new Dictionary<SymbolReferenceKind, string>
    {
        [SymbolReferenceKind.Call] = "call",
        [SymbolReferenceKind.TypeUse] = "type-use",
        [SymbolReferenceKind.Inheritance] = "inheritance",
        [SymbolReferenceKind.Import] = "import",
        [SymbolReferenceKind.Use] = "use",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, SymbolReferenceKind> s_referenceByName =
        s_referenceNames.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The stable lower-case name of a kind (<c>class</c>, <c>method</c>, <c>enum-member</c>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name; the number for a value outside the enumeration.</returns>
    public static string Name(SymbolKind kind) => s_names.TryGetValue(kind, out var name) ? name : ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The stable lower-case name of a reference kind (<c>call</c>, <c>type-use</c>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name; the number for a value outside the enumeration.</returns>
    public static string Name(SymbolReferenceKind kind) => s_referenceNames.TryGetValue(kind, out var name) ? name : ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses a name produced by <see cref="Name(SymbolKind)"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>True when the name is known.</returns>
    public static bool TryParse(string? name, out SymbolKind kind) => s_byName.TryGetValue(name ?? string.Empty, out kind);

    /// <summary>Parses a name produced by <see cref="Name(SymbolReferenceKind)"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>True when the name is known.</returns>
    public static bool TryParse(string? name, out SymbolReferenceKind kind) => s_referenceByName.TryGetValue(name ?? string.Empty, out kind);

    /// <summary>True for kinds that name a type (and so can be a base type, a type argument or a constructed object).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for classes, structs, interfaces, enums, delegates, aliases and other types.</returns>
    public static bool IsType(SymbolKind kind) => kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface
        or SymbolKind.Enum or SymbolKind.Delegate or SymbolKind.TypeAlias or SymbolKind.Type;

    /// <summary>True for kinds whose body is code (functions, methods, constructors, procedures, macros).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for callable kinds.</returns>
    public static bool IsCallable(SymbolKind kind) => kind is SymbolKind.Function or SymbolKind.Method
        or SymbolKind.Constructor or SymbolKind.Macro or SymbolKind.Procedure;

    /// <summary>True for kinds that contain other definitions (namespaces, modules and types).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for container kinds.</returns>
    public static bool IsContainer(SymbolKind kind) => kind is SymbolKind.Namespace or SymbolKind.Module || IsType(kind);

    /// <summary>
    /// Whether a definition of <paramref name="target"/> kind can be what a reference of <paramref name="reference"/>
    /// kind names: calls name callables and types (constructions), type uses and inheritance name types, imports and
    /// other uses name anything.
    /// </summary>
    /// <param name="reference">The reference kind.</param>
    /// <param name="target">The definition kind.</param>
    /// <returns>True when compatible.</returns>
    public static bool IsCompatible(SymbolReferenceKind reference, SymbolKind target) => reference switch
    {
        SymbolReferenceKind.Call => IsCallable(target) || IsType(target),
        SymbolReferenceKind.TypeUse => IsType(target) || target is SymbolKind.Table or SymbolKind.View,
        SymbolReferenceKind.Inheritance => IsType(target),
        _ => true,
    };
}
