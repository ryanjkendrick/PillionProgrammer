using System.Text;

namespace Pillion.ContextEngine.Tests.Symbols;

/// <summary>
/// Generated C# code for the symbol benchmarks: classes with fields, properties and methods that construct and call
/// each other across files, so the reference graph has real structure (a few widely used "core" services and many
/// leaf services). Deterministic: the same arguments always produce the same text.
/// </summary>
internal static class SyntheticRepository
{
    /// <summary>One file of about <paramref name="lines"/> lines whose classes refer to each other.</summary>
    public static string SingleFile(int lines) => File(0, 1, lines, out _);

    /// <summary><paramref name="files"/> files of about <paramref name="linesPerFile"/> lines each, under <c>src/ModuleN/</c>.</summary>
    public static List<(string Path, string Text)> Files(int files, int linesPerFile)
    {
        var result = new List<(string, string)>(files);
        for (var i = 0; i < files; i++)
        {
            var text = File(i, files, linesPerFile, out _);
            result.Add(($"src/Module{i % 10}/File{i}.cs", text));
        }

        return result;
    }

    private static string File(int fileIndex, int fileCount, int lines, out int written)
    {
        var builder = new StringBuilder(lines * 40);
        builder.Append("using System;\nusing System.Collections.Generic;\n\nnamespace Generated.Module").Append(fileIndex % 10).Append(";\n\n");
        written = 5;
        var random = (uint)(fileIndex * 7919 + 17);
        for (var type = 0; written < lines; type++)
        {
            var name = $"Service{fileIndex}_{type}";
            builder.Append("/// <summary>Type ").Append(type).Append(".</summary>\npublic sealed class ").Append(name).Append(" : IDisposable\n{\n");
            builder.Append("    private readonly List<int> _items = new();\n    private int _count;\n\n");
            builder.Append("    public int Count => _count;\n\n");
            written += 8;
            for (var method = 0; method < 4; method++)
            {
                // Most calls go to a handful of core services (file 0, types 0-2); the rest anywhere.
                random = (random * 1103515245) + 12345;
                var core = (random >> 16) % 3 == 0;
                var targetFile = core ? 0 : (int)((random >> 8) % (uint)fileCount);
                var targetType = core ? (int)((random >> 4) % 3) : (int)((random >> 12) % 8);
                var targetMethod = (int)((random >> 20) % 4);
                builder.Append("    /// <summary>Method ").Append(method).Append(".</summary>\n    public int Compute").Append(method).Append("(int value)\n    {\n");
                builder.Append("        var total = value * ").Append(method + 1).Append(";\n");
                builder.Append("        total += new Service").Append(targetFile).Append('_').Append(targetType).Append("().Compute").Append(targetMethod).Append("(total);\n");
                builder.Append("        foreach (var item in _items)\n        {\n            total += item;\n        }\n\n");
                builder.Append("        _count++;\n        return total;\n    }\n\n");
                written += 14;
            }

            builder.Append("    public void Dispose() => _items.Clear();\n}\n\n");
            written += 3;
        }

        return builder.ToString();
    }
}
