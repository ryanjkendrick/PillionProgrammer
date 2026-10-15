using System;
using System.IO;
using System.Text;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The <c>.pillionignore</c> file at a repository root (design section 7): paths Pillion never indexes, in the syntax of
/// <c>.gitignore</c>. The Index window opens it for editing; it is created, with a one-line comment, only when the
/// developer asks to open it and it does not exist.
/// </summary>
public static class PillionIgnoreFile
{
    /// <summary>The file's name.</summary>
    public const string FileName = ".pillionignore";

    /// <summary>The comment a new file starts with.</summary>
    public const string NewFileHeader = "# Pillion Programmer does not index paths listed here. The syntax is the one of .gitignore.";

    /// <summary>Gets the file's path for a repository root.</summary>
    /// <param name="rootPath">The repository root.</param>
    /// <returns>The path of <c>.pillionignore</c> in the root.</returns>
    public static string PathFor(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A repository root is required.", nameof(rootPath));
        }

        return Path.Combine(rootPath, FileName);
    }

    /// <summary>Creates the file with <see cref="NewFileHeader"/> if it does not exist yet.</summary>
    /// <param name="rootPath">The repository root, which must exist.</param>
    /// <returns>The path of the file.</returns>
    /// <exception cref="IOException">The file cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The root is read-only.</exception>
    public static string EnsureExists(string rootPath)
    {
        var path = PathFor(rootPath);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, NewFileHeader + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return path;
    }
}
