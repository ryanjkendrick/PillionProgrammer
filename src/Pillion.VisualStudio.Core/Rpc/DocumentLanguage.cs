using System;
using System.Collections.Generic;
using System.IO;

namespace Pillion.VisualStudio.Core;

/// <summary>Maps a document path to a language identifier for <see cref="Contracts.OpenDocument.LanguageId"/>.</summary>
public static class DocumentLanguage
{
    /// <summary>The identifier for documents of an unknown type.</summary>
    public const string PlainText = "plaintext";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".vb"] = "vb",
        [".fs"] = "fsharp",
        [".fsi"] = "fsharp",
        [".fsx"] = "fsharp",
        [".c"] = "c",
        [".h"] = "cpp",
        [".hpp"] = "cpp",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".xaml"] = "xaml",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".vbproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".json"] = "json",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescriptreact",
        [".jsx"] = "javascriptreact",
        [".css"] = "css",
        [".scss"] = "scss",
        [".html"] = "html",
        [".htm"] = "html",
        [".md"] = "markdown",
        [".sql"] = "sql",
        [".ps1"] = "powershell",
        [".py"] = "python",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
    };

    /// <summary>Gets the language identifier for a path.</summary>
    /// <param name="path">The document path.</param>
    /// <returns>A VS Code-style language identifier, or <see cref="PlainText"/>.</returns>
    public static string FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return PlainText;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return PlainText;
        }

        return ByExtension.TryGetValue(extension, out var language) ? language : PlainText;
    }
}
