using System.Text.RegularExpressions;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Sql;

namespace Pillion.ContextEngine.Symbols.Sql;

/// <summary>
/// SQL from the chunker's statement lexer. Definitions: <c>CREATE [OR REPLACE | OR ALTER] TABLE | VIEW | PROCEDURE |
/// FUNCTION name</c>, with the schema (<c>dbo</c>) as the container. References: table names after <c>FROM</c> (and
/// the comma list that follows), <c>JOIN</c>, <c>UPDATE</c>, <c>INTO</c>, <c>INSERT</c>, <c>DELETE</c>, <c>MERGE</c>,
/// <c>USING</c>, <c>REFERENCES</c> and <c>ALTER TABLE</c> (uses), and procedures after <c>EXEC</c> (calls). Comments
/// and string literals are ignored; bracketed, quoted and back-quoted names are unquoted.
/// </summary>
public sealed partial class SqlSymbolExtractor : SyntaxSymbolExtractor
{
    private const string Part = @"(?:\[[^\]\r\n]+\]|""[^""\r\n]+""|`[^`\r\n]+`|[A-Za-z_#][\w$#]*)";
    private const string Name = @"(?<name>" + Part + @"(?:\s*\.\s*" + Part + @"){0,3})";

    /// <inheritdoc />
    public override string Language => LanguageIds.Sql;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        failure = null;
        var text = sink.Document.Text;
        var masked = Mask(text);
        foreach (var (start, end) in SqlChunker.Statements(text))
        {
            var statement = masked.Substring(start, end - start);
            var from = string.Empty;
            var definition = Definition().Match(statement);
            if (definition.Success)
            {
                var (name, schema) = Split(definition.Groups["name"].Value);
                var kind = definition.Groups["kind"].Value.ToUpperInvariant() switch
                {
                    "TABLE" => SymbolKind.Table,
                    "VIEW" => SymbolKind.View,
                    "FUNCTION" => SymbolKind.Function,
                    _ => SymbolKind.Procedure,
                };

                var headerEnd = kind == SymbolKind.Table
                    ? IndexOrEnd(statement, statement.IndexOf('(', definition.Index + definition.Length))
                    : HeaderStop().Match(statement, definition.Index + definition.Length) is { Success: true } stop ? stop.Index : statement.Length;
                var container = schema ?? string.Empty;
                sink.Define(name, kind, container, start, end, SignatureText.Normalize(statement.AsSpan(definition.Index, Math.Min(headerEnd, definition.Index + 800) - definition.Index)));
                from = container.Length == 0 ? name : container + Breadcrumbs.Separator + name;
            }

            foreach (Match match in TableReference().Matches(statement))
            {
                if (definition.Success && match.Index < definition.Index + definition.Length && match.Index >= definition.Index)
                {
                    continue;
                }

                AddReference(sink, match.Groups["name"], start, SymbolReferenceKind.Use, from);
                if (match.Groups["keyword"].Value.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                {
                    // FROM a, b, c
                    var position = match.Index + match.Length;
                    while (CommaName().Match(statement, position) is { Success: true } next && next.Index == position)
                    {
                        AddReference(sink, next.Groups["name"], start, SymbolReferenceKind.Use, from);
                        position = next.Index + next.Length;
                    }
                }
            }

            foreach (Match match in Execute().Matches(statement))
            {
                AddReference(sink, match.Groups["name"], start, SymbolReferenceKind.Call, from);
            }
        }

        return true;
    }

    private static void AddReference(SymbolSink sink, Group group, int statementStart, SymbolReferenceKind kind, string from)
    {
        var (name, qualifier) = Split(group.Value);
        if (name.Length == 0 || IsKeyword(name))
        {
            return;
        }

        sink.Reference(name, kind, statementStart + group.Index, qualifier, from);
    }

    /// <summary>A possibly qualified, possibly quoted name: the last part, and the part before it.</summary>
    private static (string Name, string? Qualifier) Split(string qualified)
    {
        var parts = new List<string>();
        foreach (Match part in NamePart().Matches(qualified))
        {
            parts.Add(part.Value.Trim('[', ']', '"', '`'));
        }

        return parts.Count switch
        {
            0 => (string.Empty, null),
            1 => (parts[0], null),
            _ => (parts[^1], parts[^2]),
        };
    }

    private static int IndexOrEnd(string text, int index) => index < 0 ? text.Length : index;

    /// <summary>The text with comments and string literals replaced by spaces (same length, line breaks kept).</summary>
    private static string Mask(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
            {
                while (i < chars.Length && chars[i] is not ('\r' or '\n'))
                {
                    chars[i++] = ' ';
                }
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? chars.Length : close + 2;
                Blank(chars, i, end);
                i = end;
            }
            else if (c == '\'')
            {
                var j = i + 1;
                while (j < chars.Length && !(chars[j] == '\'' && (j + 1 >= chars.Length || chars[j + 1] != '\'')))
                {
                    j += chars[j] == '\'' ? 2 : 1;
                }

                var end = Math.Min(chars.Length, j + 1);
                Blank(chars, i, end);
                i = end;
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int start, int end)
    {
        for (var k = start; k < end && k < chars.Length; k++)
        {
            if (chars[k] is not ('\r' or '\n'))
            {
                chars[k] = ' ';
            }
        }
    }

    private static bool IsKeyword(string name) => name.ToUpperInvariant() is "SELECT" or "SET" or "WHERE" or "VALUES" or "DEFAULT"
        or "CASCADE" or "NO" or "ACTION" or "RESTRICT" or "NULL" or "TOP" or "DISTINCT" or "ALL" or "OUTPUT" or "LATERAL"
        or "ONLY" or "TABLE" or "INTO" or "FROM" or "WITH" or "AS" or "ON" or "OPENJSON" or "OPENROWSET" or "UNNEST" or "DUAL";

    [GeneratedRegex(@"\A\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP(?:ORARY)?\s+)?(?:MATERIALIZED\s+)?(?<kind>TABLE|VIEW|PROCEDURE|PROC|FUNCTION)\s+(?:IF\s+NOT\s+EXISTS\s+)?" + Name,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Definition();

    [GeneratedRegex(@"\b(?:AS|BEGIN|IS|LANGUAGE|RETURNS\s+TABLE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderStop();

    [GeneratedRegex(@"\b(?<keyword>FROM|JOIN|UPDATE|INTO|REFERENCES|USING|ALTER\s+TABLE|INSERT(?!\s+INTO\b)|DELETE(?!\s+FROM\b)|MERGE(?!\s+INTO\b))\s+" + Name,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableReference();

    [GeneratedRegex(@"\s*,\s*" + Name, RegexOptions.CultureInvariant)]
    private static partial Regex CommaName();

    [GeneratedRegex(@"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?" + Name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Execute();

    [GeneratedRegex(Part, RegexOptions.CultureInvariant)]
    private static partial Regex NamePart();
}
