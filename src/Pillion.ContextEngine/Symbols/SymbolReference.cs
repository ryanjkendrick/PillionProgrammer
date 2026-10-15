using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// A use of a name found in a file's syntax: a call, a type in a declaration, a base type, an import. References are
/// by name only (there is no semantic model); <see cref="SymbolGraphBuilder"/> resolves them to candidate definitions
/// across the repository and turns them into <c>symbol_refs</c> edges.
/// </summary>
public sealed record SymbolReference
{
    /// <summary>The file the reference is in, exactly as the caller passed it to the extractor.</summary>
    public required string FilePath { get; init; }

    /// <summary>The line of the referenced name, 1-based.</summary>
    public required int Line { get; init; }

    /// <summary>
    /// The referenced name as written, without type arguments: <c>Greet</c> for <c>greeter.Greet(x)</c>,
    /// <c>List</c> for <c>List&lt;int&gt;</c>. Imports of namespaces and packages keep their dotted form
    /// (<c>System.Collections.Generic</c>); module imports use the module's file stem (<c>logger</c> for
    /// <c>"./logger"</c>, <c>list.h</c> for <c>#include "list.h"</c> in C and C++).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>How the name is used.</summary>
    public required SymbolReferenceKind Kind { get; init; }

    /// <summary>
    /// The simple name written before the referenced name, when there is one: <c>greeter</c> in
    /// <c>greeter.Greet()</c>, <c>Point</c> in <c>Point::new()</c>, <c>util</c> in <c>java.util.List</c>. It may be a
    /// type, a namespace or a variable; the resolver uses it to prefer definitions whose container has that name.
    /// Null when the name stands alone or follows <c>this</c>, <c>self</c> or <c>base</c>.
    /// </summary>
    public string? Qualifier { get; init; }

    /// <summary>
    /// The breadcrumb of the code the reference is in (containers and the enclosing member, joined by
    /// <c>" &gt; "</c>, without the path), for example <c>Fixtures.Sample &gt; Greeter&lt;T&gt; &gt; GreetAll</c>.
    /// Empty for file-level code (imports, top-level statements).
    /// </summary>
    public string FromContainer { get; init; } = string.Empty;

    /// <summary>The language identifier (one of <see cref="LanguageIds"/>).</summary>
    public string Language { get; init; } = LanguageIds.Text;
}
