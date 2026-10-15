namespace Pillion.ContextEngine;

/// <summary>
/// Versions of the Context Engine's derived output. They are part of the index identity (design section 10: an index
/// is keyed by repository root, embedding model and chunker version), so changing one rebuilds every index.
/// </summary>
public static class ContextEngineVersions
{
    /// <summary>
    /// The version of the chunking and symbol extraction rules, read by repository discovery (work item 2.3) into the
    /// index identity and the index <c>meta</c> table. Bump it (to the next integer) whenever the same file would produce
    /// different stored output: chunk boundaries, kinds, breadcrumbs or text; the default token counting; the
    /// extension-to-language map; extracted definitions, references, symbol kinds, containers, signatures or symbol ids.
    /// Do not bump it for changes that leave the output identical (refactoring, performance work, new tests). See the
    /// README.
    /// </summary>
    public const string ChunkerVersion = "1";
}
