namespace Pillion.ContextEngine;

/// <summary>
/// Versions of the Context Engine's derived output, recorded in the index so a build knows when stored data was
/// produced by different rules. See the README for the bump rules.
/// </summary>
public static class ContextEngineVersions
{
    /// <summary>
    /// The version of the chunking rules. It is part of the index identity (design section 10: an index is keyed by
    /// repository root, embedding model and chunker version), read by repository discovery (work item 2.3) and written
    /// to the index <c>meta</c> table as <c>chunker_version</c>; changing it builds a new index, embeddings included.
    /// Bump it (to the next integer) whenever the same file would produce different chunks: boundaries, kinds,
    /// breadcrumbs or text, splitting, the window settings, the default token counting, the extension-to-language map,
    /// or a chunking grammar description. Symbol changes do not bump it; they bump <see cref="SymbolsVersion"/>.
    /// </summary>
    public const string ChunkerVersion = "1";

    /// <summary>
    /// The version of the symbol rules: extraction (which definitions and references, their kinds, names, containers,
    /// lines and signatures, the symbol grammar descriptions), the symbol id formula, and reference resolution (which
    /// decides the stored <c>symbol_refs</c> edges). The host writes it to the index <c>meta</c> table as
    /// <c>symbols_version</c>, separately from <c>chunker_version</c>; when the stored value differs, only the
    /// <c>symbols</c> and <c>symbol_refs</c> tables are rebuilt (no re-chunking, no re-embedding). Bump it (to the next
    /// integer) for any of those changes. Do not bump it for changes that leave the stored symbols identical
    /// (refactoring, performance work, tests) or that only change what is computed from them (ranking, the repo map,
    /// the query helpers).
    /// </summary>
    public const string SymbolsVersion = "1";
}
