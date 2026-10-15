using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// A definition found in a file's syntax: a namespace, type, member, top-level variable, SQL table and so on. It has
/// the shape of a <c>symbols</c> row (design section 10) except for the file id, which the indexing pipeline maps from
/// <see cref="FilePath"/>. Everything syntax-derived is approximate (<see cref="IsApproximate"/>): there is no
/// compilation, so overloads, partial types and generated members are seen only as the text shows them.
/// </summary>
public sealed record SymbolDefinition
{
    /// <summary>Creates a definition and computes its <see cref="Id"/>.</summary>
    /// <param name="filePath">The file path, exactly as the caller passed it to the extractor.</param>
    /// <param name="name">The name, without type parameters (<c>Greeter</c>, not <c>Greeter&lt;T&gt;</c>).</param>
    /// <param name="kind">The kind.</param>
    /// <param name="container">The container breadcrumb, outermost first, joined by <see cref="Breadcrumbs.Separator"/>.</param>
    /// <param name="startLine">The first line, 1-based.</param>
    /// <param name="endLine">The last line, 1-based and inclusive.</param>
    /// <param name="signature">The declaration header on one line.</param>
    /// <param name="language">The language identifier.</param>
    /// <param name="isApproximate">False only for definitions from a compiler-backed source.</param>
    public SymbolDefinition(string filePath, string name, SymbolKind kind, string container, int startLine, int endLine,
        string signature = "", string language = LanguageIds.Text, bool isApproximate = true)
    {
        FilePath = filePath ?? string.Empty;
        Name = name ?? string.Empty;
        Kind = kind;
        Container = container ?? string.Empty;
        StartLine = startLine;
        EndLine = Math.Max(startLine, endLine);
        Signature = signature ?? string.Empty;
        Language = language ?? LanguageIds.Text;
        IsApproximate = isApproximate;
        Id = SymbolIds.Compute(FilePath, Container, Name, Kind, StartLine);
    }

    /// <summary>The stable id: a hash of path, container, name, kind and start line (see <see cref="SymbolIds"/>).</summary>
    public long Id { get; }

    /// <summary>The file path, exactly as the caller passed it to the extractor.</summary>
    public string FilePath { get; }

    /// <summary>The name, without type parameters. Dotted for namespaces declared with a dotted name (<c>Fixtures.Sample</c>).</summary>
    public string Name { get; }

    /// <summary>The kind.</summary>
    public SymbolKind Kind { get; }

    /// <summary>
    /// The container breadcrumb: the namespaces and types around the definition, outermost first, joined by
    /// <c>" &gt; "</c>, with the same segments as the chunk breadcrumb (so <c>Fixtures.Sample &gt; Greeter&lt;T&gt;</c>
    /// for a member of <c>Greeter&lt;T&gt;</c>) but without the path. Empty at file scope.
    /// </summary>
    public string Container { get; }

    /// <summary>The first line, 1-based (the declaration itself, after any doc comment or attribute).</summary>
    public int StartLine { get; }

    /// <summary>The last line, 1-based and inclusive.</summary>
    public int EndLine { get; }

    /// <summary>
    /// The declaration header on one line, white space collapsed, without attributes, body or trailing brace, at most
    /// <see cref="SignatureText.MaxLength"/> characters (for example <c>public string Greet(string name)</c>).
    /// </summary>
    public string Signature { get; }

    /// <summary>The language identifier (one of <see cref="LanguageIds"/>).</summary>
    public string Language { get; }

    /// <summary>True for everything derived from syntax alone (always, for the extractors in this library).</summary>
    public bool IsApproximate { get; }

    /// <summary>The container and the name: <c>Fixtures.Sample &gt; Greeter&lt;T&gt; &gt; Greet</c>.</summary>
    public string QualifiedName => Container.Length == 0 ? Name : Container + Breadcrumbs.Separator + Name;
}
