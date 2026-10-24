using System.Collections.Concurrent;
using TsLanguage = TreeSitter.Language;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// Loads each grammar's native library once per process and remembers a failure, so a missing grammar costs one
/// exception, not one per file. Loaded languages live for the process (they are small and read-only, and parsers
/// on several threads may share one).
/// </summary>
internal sealed class GrammarLoader
{
    private static readonly ConcurrentDictionary<string, GrammarLoader> s_loaders = new(StringComparer.Ordinal);

    private readonly Lazy<(TsLanguage? Language, string? Error)> _state;

    private GrammarLoader(string grammarId)
    {
        _state = new Lazy<(TsLanguage?, string?)>(() => Load(grammarId), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public TsLanguage? Language => _state.Value.Language;

    public string? Error => _state.Value.Error;

    public static GrammarLoader For(string grammarId) => s_loaders.GetOrAdd(grammarId, id => new GrammarLoader(id));

    private static (TsLanguage?, string?) Load(string grammarId)
    {
        try
        {
            TreeSitterNative.EnsureRegistered();
            return (new TsLanguage(grammarId), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, $"The tree-sitter grammar '{grammarId}' could not be loaded: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
