using TreeSitter;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>Temporary smoke check.</summary>
public static class Smoke
{
    /// <summary>Parses a snippet.</summary>
    /// <param name="id">Grammar id.</param>
    /// <param name="code">Code.</param>
    /// <returns>The S-expression.</returns>
    public static string Parse(string id, string code)
    {
        TreeSitterNative.EnsureRegistered();
        using var language = new Language(id);
        using var parser = new Parser(language);
        using var tree = parser.Parse(code)!;
        return tree.RootNode.ToString();
    }
}
