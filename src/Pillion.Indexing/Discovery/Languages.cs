namespace Pillion.Indexing.Discovery;

/// <summary>
/// The languages the indexing pipeline distinguishes (design section 7, Parse and chunk), detected from the file name's
/// extension alone. Anything else is <see cref="Other"/>, which the chunker splits into 60-line windows.
/// </summary>
public static class Languages
{
    /// <summary>C#.</summary>
    public const string CSharp = "csharp";

    /// <summary>Visual Basic.</summary>
    public const string VisualBasic = "vb";

    /// <summary>Razor (<c>.cshtml</c>, <c>.razor</c>), split into its C# and markup parts.</summary>
    public const string Razor = "razor";

    /// <summary>TypeScript, including TSX.</summary>
    public const string TypeScript = "typescript";

    /// <summary>JavaScript, including JSX and the module variants.</summary>
    public const string JavaScript = "javascript";

    /// <summary>Python.</summary>
    public const string Python = "python";

    /// <summary>C source (<c>.c</c>).</summary>
    public const string C = "c";

    /// <summary>C++ and header files (<c>.h</c> included: a C++ grammar parses C headers, not the other way round).</summary>
    public const string Cpp = "cpp";

    /// <summary>Go.</summary>
    public const string Go = "go";

    /// <summary>Rust.</summary>
    public const string Rust = "rust";

    /// <summary>Java.</summary>
    public const string Java = "java";

    /// <summary>SQL.</summary>
    public const string Sql = "sql";

    /// <summary>HTML.</summary>
    public const string Html = "html";

    /// <summary>CSS.</summary>
    public const string Css = "css";

    /// <summary>Everything else: Markdown, JSON, XML, project files and so on.</summary>
    public const string Other = "other";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = CSharp,
        [".csx"] = CSharp,
        [".vb"] = VisualBasic,
        [".cshtml"] = Razor,
        [".razor"] = Razor,
        [".ts"] = TypeScript,
        [".tsx"] = TypeScript,
        [".mts"] = TypeScript,
        [".cts"] = TypeScript,
        [".js"] = JavaScript,
        [".jsx"] = JavaScript,
        [".mjs"] = JavaScript,
        [".cjs"] = JavaScript,
        [".py"] = Python,
        [".pyi"] = Python,
        [".c"] = C,
        [".cpp"] = Cpp,
        [".cc"] = Cpp,
        [".cxx"] = Cpp,
        [".c++"] = Cpp,
        [".h"] = Cpp,
        [".hpp"] = Cpp,
        [".hh"] = Cpp,
        [".hxx"] = Cpp,
        [".inl"] = Cpp,
        [".ipp"] = Cpp,
        [".go"] = Go,
        [".rs"] = Rust,
        [".java"] = Java,
        [".sql"] = Sql,
        [".html"] = Html,
        [".htm"] = Html,
        [".css"] = Css,
    };

    /// <summary>Gets every language identifier, <see cref="Other"/> last.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CSharp, VisualBasic, Razor, TypeScript, JavaScript, Python, C, Cpp, Go, Rust, Java, Sql, Html, Css, Other,
    ];

    /// <summary>Detects the language of a file from its name.</summary>
    /// <param name="path">A file name or path; only the extension counts, and case is ignored.</param>
    /// <returns>A language identifier from this class; <see cref="Other"/> for an unknown or missing extension.</returns>
    public static string Detect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var extension = Path.GetExtension(path.AsSpan());
        return extension.IsEmpty || !ByExtension.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension, out var language)
            ? Other
            : language;
    }
}
