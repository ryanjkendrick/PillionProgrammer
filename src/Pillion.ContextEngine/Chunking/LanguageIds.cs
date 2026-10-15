namespace Pillion.ContextEngine.Chunking;

/// <summary>The language identifiers recorded on chunks.</summary>
public static class LanguageIds
{
    /// <summary>Any text file without a syntactic chunker.</summary>
    public const string Text = "text";

    /// <summary>C#.</summary>
    public const string CSharp = "csharp";

    /// <summary>Visual Basic.</summary>
    public const string VisualBasic = "vb";

    /// <summary>Razor components and views.</summary>
    public const string Razor = "razor";

    /// <summary>TypeScript.</summary>
    public const string TypeScript = "typescript";

    /// <summary>TypeScript with JSX.</summary>
    public const string Tsx = "tsx";

    /// <summary>JavaScript, including JSX.</summary>
    public const string JavaScript = "javascript";

    /// <summary>Python.</summary>
    public const string Python = "python";

    /// <summary>C.</summary>
    public const string C = "c";

    /// <summary>C++ (also used for <c>.h</c> headers, whose grammar is a superset of C's for chunking purposes).</summary>
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
}
