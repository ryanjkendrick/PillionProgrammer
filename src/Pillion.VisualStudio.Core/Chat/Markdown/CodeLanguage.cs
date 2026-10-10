using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Markdown;

/// <summary>
/// The language of a fenced code block, mapped from the fence's tag (<c>```csharp</c>): a display name for the tooltip and
/// the Visual Studio content types to try, in order, for the embedded editor view (then the file extension, then plain
/// text). One instance per language, so instances can be compared by reference.
/// </summary>
public sealed class CodeLanguage
{
    /// <summary>The Visual Studio plain text content type name.</summary>
    public const string PlainTextContentType = "text";

    private static readonly Dictionary<string, CodeLanguage> ByTag = new(StringComparer.OrdinalIgnoreCase);

    private CodeLanguage(string id, string displayName, string fileExtension, params string[] contentTypes)
    {
        Id = id;
        DisplayName = displayName;
        FileExtension = fileExtension;
        ContentTypes = contentTypes;
    }

    /// <summary>Gets C# (<c>csharp</c>, <c>cs</c>, <c>c#</c>).</summary>
    public static CodeLanguage CSharp { get; } = Register(new("csharp", "C#", ".cs", "CSharp"), "csharp", "cs", "c#", "c-sharp");

    /// <summary>Gets Visual Basic (<c>vb</c>, <c>vbnet</c>).</summary>
    public static CodeLanguage VisualBasic { get; } = Register(new("vb", "Visual Basic", ".vb", "Basic"), "vb", "vbnet", "vb.net", "visualbasic");

    /// <summary>Gets F# (<c>fsharp</c>, <c>fs</c>, <c>f#</c>).</summary>
    public static CodeLanguage FSharp { get; } = Register(new("fsharp", "F#", ".fs", "F#"), "fsharp", "fs", "f#");

    /// <summary>Gets TypeScript (<c>ts</c>, <c>typescript</c>, <c>tsx</c>).</summary>
    public static CodeLanguage TypeScript { get; } = Register(new("typescript", "TypeScript", ".ts", "TypeScript"), "ts", "typescript", "tsx");

    /// <summary>Gets JavaScript (<c>js</c>, <c>javascript</c>, <c>jsx</c>).</summary>
    public static CodeLanguage JavaScript { get; } = Register(new("javascript", "JavaScript", ".js", "JavaScript"), "js", "javascript", "jsx", "node");

    /// <summary>Gets JSON (<c>json</c>, <c>jsonc</c>).</summary>
    public static CodeLanguage Json { get; } = Register(new("json", "JSON", ".json", "JSON"), "json", "jsonc", "json5");

    /// <summary>Gets XML (<c>xml</c>, <c>xaml</c>, <c>csproj</c>).</summary>
    public static CodeLanguage Xml { get; } = Register(new("xml", "XML", ".xml", "XML"), "xml", "xaml", "csproj", "msbuild", "svg");

    /// <summary>Gets HTML (<c>html</c>, <c>htm</c>).</summary>
    public static CodeLanguage Html { get; } = Register(new("html", "HTML", ".html", "HTMLX", "HTML"), "html", "htm", "xhtml");

    /// <summary>Gets CSS (<c>css</c>).</summary>
    public static CodeLanguage Css { get; } = Register(new("css", "CSS", ".css", "CSS"), "css");

    /// <summary>Gets Python (<c>python</c>, <c>py</c>).</summary>
    public static CodeLanguage Python { get; } = Register(new("python", "Python", ".py", "Python"), "python", "py", "python3");

    /// <summary>Gets PowerShell (<c>powershell</c>, <c>ps1</c>, <c>pwsh</c>).</summary>
    public static CodeLanguage PowerShell { get; } = Register(new("powershell", "PowerShell", ".ps1", "PowerShell"), "powershell", "ps1", "ps", "pwsh", "posh");

    /// <summary>Gets Bash and other shells (<c>bash</c>, <c>sh</c>, <c>shell</c>).</summary>
    public static CodeLanguage Bash { get; } = Register(new("bash", "Bash", ".sh"), "bash", "sh", "shell", "zsh", "console", "shellscript");

    /// <summary>Gets SQL (<c>sql</c>, <c>tsql</c>).</summary>
    public static CodeLanguage Sql { get; } = Register(new("sql", "SQL", ".sql", "SQL Server Tools", "SQL"), "sql", "tsql", "t-sql", "mssql");

    /// <summary>Gets Markdown (<c>markdown</c>, <c>md</c>).</summary>
    public static CodeLanguage Markdown { get; } = Register(new("markdown", "Markdown", ".md", "markdown"), "markdown", "md");

    /// <summary>Gets plain text: no tag, or a tag not listed here.</summary>
    public static CodeLanguage PlainText { get; } = Register(new("text", string.Empty, string.Empty, PlainTextContentType), "text", "txt", "plaintext", "plain");

    /// <summary>Gets an identifier for the language (<c>csharp</c>, <c>text</c>, ...).</summary>
    public string Id { get; }

    /// <summary>Gets the language's name for the tooltip ("C#"); empty for plain text (the view then names the tag or "Plain text").</summary>
    public string DisplayName { get; }

    /// <summary>Gets the file extension whose content type Visual Studio can look up (".cs"), or empty for plain text.</summary>
    public string FileExtension { get; }

    /// <summary>Gets the Visual Studio content type names to try, in order (none for languages without a well-known one).</summary>
    public IReadOnlyList<string> ContentTypes { get; }

    /// <summary>Gets a value indicating whether this is <see cref="PlainText"/>.</summary>
    public bool IsPlainText => ReferenceEquals(this, PlainText);

    /// <summary>Maps a fence's info string to a language: the first word, case-insensitive, with braces and a leading dot removed.</summary>
    /// <param name="info">The info string after the opening fence (for example <c>csharp</c> or <c>{.python}</c>).</param>
    /// <returns>The language; <see cref="PlainText"/> when the tag is missing or not known.</returns>
    public static CodeLanguage FromFenceInfo(string? info)
    {
        var tag = NormalizeTag(info);
        return tag.Length > 0 && ByTag.TryGetValue(tag, out var language) ? language : PlainText;
    }

    /// <summary>Extracts the tag from a fence's info string (the first word, without braces or a leading dot).</summary>
    /// <param name="info">The info string.</param>
    /// <returns>The tag, or an empty string.</returns>
    public static string NormalizeTag(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return string.Empty;
        }

        var tag = info!.Trim();
        var space = tag.IndexOfAny([' ', '\t', ',']);
        if (space >= 0)
        {
            tag = tag.Substring(0, space);
        }

        tag = tag.Trim('{', '}');
        if (tag.StartsWith(".", StringComparison.Ordinal))
        {
            tag = tag.Substring(1);
        }

        if (tag.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
        {
            tag = tag.Substring("language-".Length);
        }

        return tag;
    }

    /// <summary>Gets the name shown in the code block's header and tooltip: the language name, else the tag as written, else "Plain text".</summary>
    /// <param name="info">The fence's info string.</param>
    /// <returns>The name.</returns>
    public string NameFor(string? info)
    {
        if (DisplayName.Length > 0)
        {
            return DisplayName;
        }

        var tag = NormalizeTag(info);
        return tag.Length > 0 && !ByTag.ContainsKey(tag) ? tag : ChatStrings.PlainTextLanguage;
    }

    /// <inheritdoc/>
    public override string ToString() => Id;

    private static CodeLanguage Register(CodeLanguage language, params string[] tags)
    {
        foreach (var tag in tags)
        {
            ByTag[tag] = language;
        }

        return language;
    }
}
