using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Razor;
using Pillion.ContextEngine.Symbols.Roslyn;

namespace Pillion.ContextEngine.Symbols.Razor;

/// <summary>
/// Razor components and views (<c>.razor</c>, <c>.cshtml</c>). The file defines a class named after the file (the
/// component); the members of <c>@code</c> and <c>@functions</c> blocks are read by the C# rules with the component as
/// their container, and <c>@{ }</c> blocks contribute references from <c>Component &gt; @{ }</c>, as their chunks are
/// named. In the markup: <c>@using</c> (import), <c>@inherits</c> and <c>@implements</c> (inheritance), <c>@inject</c>,
/// <c>@model</c> and <c>@layout</c> (type uses), component tags such as <c>&lt;Counter /&gt;</c> (type uses), and
/// <c>@name</c> expressions and <c>@onclick="Handler"</c> attributes (uses).
/// </summary>
public sealed partial class RazorSymbolExtractor : SyntaxSymbolExtractor
{
    private const string MembersPrefix = "class __RazorBlock {";
    private const string StatementsPrefix = "class __RazorBlock { void __Render() {";
    private const string StatementsMember = "@{ }";

    private static readonly CSharpParseOptions s_parseOptions = new(LanguageVersion.Preview, Microsoft.CodeAnalysis.DocumentationMode.None);

    /// <inheritdoc />
    public override string Language => LanguageIds.Razor;

    private protected override bool Collect(SymbolSink sink, out string? failure)
    {
        failure = null;
        var document = sink.Document;
        var text = document.Text;
        var blocks = RazorScanner.Scan(text);
        var component = ComponentName(document.Path);
        sink.DefineLines(component, SymbolKind.Class, string.Empty, 1, document.LineCount, "component " + component);

        foreach (var block in blocks)
        {
            var contentStart = block.Open + 1;
            var contentLength = Math.Max(0, Math.Min(block.Close, text.Length) - contentStart);
            var prefix = block.Kind == RazorBlockKind.Members ? MembersPrefix : StatementsPrefix;
            var suffix = block.Kind == RazorBlockKind.Members ? "\n}" : "\n}}";
            var source = string.Concat(prefix, text.AsSpan(contentStart, contentLength), suffix);
            var tree = CSharpSyntaxTree.ParseText(source, s_parseOptions);
            if (tree.GetRoot() is not CompilationUnitSyntax { Members: [ClassDeclarationSyntax wrapper, ..] })
            {
                continue;
            }

            int Map(int position) => position < prefix.Length
                ? block.Open
                : Math.Min(contentStart + (position - prefix.Length), Math.Max(Math.Min(block.Close, text.Length), contentStart));

            var collector = new CSharpSymbolCollector(sink, tree.GetText(), Map, [component]);
            if (block.Kind == RazorBlockKind.Members)
            {
                collector.VisitMembers(wrapper.Members);
            }
            else if (wrapper.Members is [MethodDeclarationSyntax { Body: { } body }, ..])
            {
                collector.VisitBody(body, StatementsMember);
            }
        }

        CollectMarkup(sink, Mask(text, blocks), component);
        return true;
    }

    private static void CollectMarkup(SymbolSink sink, string markup, string component)
    {
        foreach (Match match in Directive().Matches(markup))
        {
            var value = match.Groups["value"].Value.Trim();
            var offset = match.Groups["value"].Index;
            switch (match.Groups["directive"].Value)
            {
                case "using":
                    if (!value.Contains('=', StringComparison.Ordinal))
                    {
                        sink.Reference(value.TrimEnd(';'), SymbolReferenceKind.Import, offset, null, string.Empty);
                    }

                    break;
                case "inherits" or "implements":
                    TypeReference(sink, value, SymbolReferenceKind.Inheritance, offset, component);
                    break;
                case "inject":
                    var type = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    TypeReference(sink, type, SymbolReferenceKind.TypeUse, offset, component);
                    break;
                default:
                    TypeReference(sink, value, SymbolReferenceKind.TypeUse, offset, component);
                    break;
            }
        }

        foreach (Match match in ComponentTag().Matches(markup))
        {
            TypeReference(sink, match.Groups["name"].Value, SymbolReferenceKind.TypeUse, match.Groups["name"].Index, component);
        }

        foreach (Match match in Expression().Matches(markup))
        {
            var name = match.Groups["name"];
            if (!IsKeyword(name.Value) && !DirectiveAttribute().IsMatch(name.Value))
            {
                sink.Reference(name.Value, SymbolReferenceKind.Use, name.Index, null, component);
            }
        }

        foreach (Match match in EventHandler().Matches(markup))
        {
            var name = match.Groups["name"];
            sink.Reference(name.Value, SymbolReferenceKind.Use, name.Index, null, component);
        }
    }

    /// <summary>A type written as text (<c>Namespace.Type&lt;T&gt;</c>): its rightmost simple name, qualified by the one before.</summary>
    private static void TypeReference(SymbolSink sink, string type, SymbolReferenceKind kind, int offset, string from)
    {
        var generic = type.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            type = type[..generic];
        }

        var parts = type.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var name = parts[^1];
        if (!SimpleName().IsMatch(name))
        {
            return;
        }

        sink.Reference(name, kind, offset + type.LastIndexOf(name, StringComparison.Ordinal), parts.Length > 1 ? parts[^2] : null, from);
    }

    /// <summary>The text with every code block replaced by spaces (line breaks kept, so offsets and lines match).</summary>
    private static string Mask(string text, List<RazorBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        foreach (var block in blocks)
        {
            var end = Math.Min(block.Close, chars.Length - 1);
            for (var i = block.Start; i <= end; i++)
            {
                if (!IsLineBreak(chars[i]))
                {
                    chars[i] = ' ';
                }
            }
        }

        return new string(chars);
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n' or (char)0x85 or (char)0x2028 or (char)0x2029;

    private static bool IsKeyword(string name) => name is "if" or "else" or "for" or "foreach" or "while" or "do" or "switch"
        or "case" or "using" or "try" or "catch" or "finally" or "lock" or "code" or "functions" or "page" or "model"
        or "inherits" or "inject" or "implements" or "layout" or "namespace" or "attribute" or "section" or "helper"
        or "typeparam" or "preservewhitespace" or "rendermode" or "addTagHelper" or "removeTagHelper" or "tagHelperPrefix"
        or "await" or "new" or "true" or "false" or "null" or "this" or "bind" or "ref" or "key" or "Html" or "Url"
        or "ViewData" or "ViewBag" or "Model";

    private static string ComponentName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "component" : name;
    }

    [GeneratedRegex(@"^[ \t]*@(?<directive>using|inherits|implements|inject|model|layout)[ \t]+(?<value>[^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    [GeneratedRegex(@"<(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)(?=[\s/>])", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentTag();

    [GeneratedRegex(@"(?<![\w@])@(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Expression();

    [GeneratedRegex(@"@on[a-z]+=""(?<name>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex EventHandler();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleName();

    /// <summary>Directive attributes (<c>@onclick</c>, <c>@onchange</c>), which are not names in the component.</summary>
    [GeneratedRegex(@"^on[a-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveAttribute();
}
