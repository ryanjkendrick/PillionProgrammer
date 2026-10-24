using System.Reflection;
using System.Runtime.InteropServices;
using TreeSitter;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// Locates the tree-sitter runtime library for the bindings. The project file copies the native libraries to
/// <c>runtimes\&lt;rid&gt;\native\</c> itself (the package's own native assets are excluded to keep the size down), so
/// they are not in the application's dependency manifest and the runtime's default probing for the bindings'
/// <c>[DllImport("tree-sitter")]</c> would not look there. The grammar libraries are loaded by the bindings' own
/// loader, which already looks in that folder.
/// </summary>
internal static class TreeSitterNative
{
    private const string RuntimeLibrary = "tree-sitter";

    private static readonly Lazy<bool> s_registered = new(Register, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Registers the import resolver once per process; safe to call from any thread.</summary>
    public static void EnsureRegistered() => _ = s_registered.Value;

    private static bool Register()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Parser).Assembly, Resolve);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Another component registered a resolver for the bindings first; default probing applies.
            return false;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, RuntimeLibrary, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        // Fall back to the runtime's default probing (for example a framework-dependent layout with deps.json).
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates()
    {
        var file = RuntimeLibrary + ".dll";
        var rid = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var roots = new List<string> { AppContext.BaseDirectory };
        var own = Path.GetDirectoryName(typeof(TreeSitterNative).Assembly.Location);
        if (!string.IsNullOrEmpty(own))
        {
            roots.Add(own);
        }

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "runtimes", rid, "native", file);
            yield return Path.Combine(root, file);
        }
    }
}
