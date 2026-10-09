using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Finds the declaration that encloses the caret through the Roslyn workspace, for C# and Visual Basic: the enclosing
/// member's doc comment and header (up to its body), with the containing type. Roslyn is supplied by Visual Studio
/// at run time (the extension compiles against Microsoft.CodeAnalysis.Workspaces.Common without shipping it); the one
/// editor entry point, <c>GetOpenDocumentInCurrentContextWithChanges</c>, is found by reflection so the extension
/// does not depend on Roslyn's editor assemblies. When anything is missing the pipeline uses its line heuristic.
/// </summary>
internal static class RoslynDeclarationProvider
{
    private const int MaxHeaderLines = 6;
    private const int MaxDocCommentLines = 8;
    private const int MaxLength = 800;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static Func<ITextSnapshot, Document?>? _getDocument;
    private static long _nextResolveTicks;

    /// <summary>Gets Roslyn's document lookup, resolving it on first use and retrying every 30 s until Roslyn is loaded.</summary>
    private static Func<ITextSnapshot, Document?>? GetDocument
    {
        get
        {
            if (Volatile.Read(ref _getDocument) is { } resolved)
            {
                return resolved;
            }

            var now = DateTime.UtcNow.Ticks;
            var next = Interlocked.Read(ref _nextResolveTicks);
            if (now < next || Interlocked.CompareExchange(ref _nextResolveTicks, now + RetryInterval.Ticks, next) != next)
            {
                return null;
            }

            var found = ResolveGetDocument();
            Volatile.Write(ref _getDocument, found);
            return found;
        }
    }

    /// <summary>Creates the lookup for a caret, or returns <see langword="null"/> for languages Roslyn does not serve.</summary>
    /// <param name="snapshot">The snapshot the caret is in.</param>
    /// <param name="position">The caret position.</param>
    /// <param name="languageId">The language identifier.</param>
    /// <returns>The lookup, or <see langword="null"/>.</returns>
    public static Func<CancellationToken, Task<string?>>? TryCreate(ITextSnapshot snapshot, int position, string languageId)
    {
        if (languageId is not ("csharp" or "vb"))
        {
            return null;
        }

        return token => LookUpAsync(snapshot, position, token);
    }

    private static async Task<string?> LookUpAsync(ITextSnapshot snapshot, int position, CancellationToken token)
    {
        if (GetDocument is not { } getDocument || getDocument(snapshot) is not { } document)
        {
            return null;
        }

        // Roslyn keeps the open document's compilation warm; the pipeline gives this lookup a short budget and falls back
        // to its line heuristic when the semantic model is not ready in time.
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var symbol = model.GetEnclosingSymbol(position, token);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } or ILocalSymbol)
        {
            symbol = symbol.ContainingSymbol;
        }

        if (symbol is null or INamespaceSymbol)
        {
            return null;
        }

        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == model.SyntaxTree)
            ?? symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }

        var node = await reference.GetSyntaxAsync(token).ConfigureAwait(false);
        var text = await node.SyntaxTree.GetTextAsync(token).ConfigureAwait(false);
        var builder = new StringBuilder();
        if (symbol.ContainingType is { } containingType)
        {
            builder.Append(document.Project.Language == LanguageNames.VisualBasic ? "' in " : "// in ")
                .Append(containingType.ToDisplayString())
                .Append('\n');
        }

        var startLine = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;

        // Doc comment lines directly above the declaration.
        var firstComment = startLine;
        while (firstComment > 0 && startLine - firstComment < MaxDocCommentLines)
        {
            var above = text.Lines[firstComment - 1].ToString().TrimStart();
            if (!above.StartsWith("///", StringComparison.Ordinal) && !above.StartsWith("'''", StringComparison.Ordinal))
            {
                break;
            }

            firstComment--;
        }

        for (var i = firstComment; i < startLine; i++)
        {
            builder.Append(text.Lines[i].ToString().Trim()).Append('\n');
        }

        // The header: from the declaration's first line up to its body.
        for (var i = startLine; i < text.Lines.Count && i < startLine + MaxHeaderLines; i++)
        {
            var line = text.Lines[i].ToString();
            var brace = line.IndexOf('{');
            if (brace >= 0)
            {
                var head = line.Substring(0, brace).Trim();
                if (head.Length > 0)
                {
                    builder.Append(head).Append('\n');
                }

                break;
            }

            builder.Append(line.Trim()).Append('\n');
            if (line.IndexOf("=>", StringComparison.Ordinal) >= 0 || document.Project.Language == LanguageNames.VisualBasic)
            {
                break;
            }
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result.Length <= MaxLength ? result : result.Substring(0, MaxLength);

    }

    /// <summary>Finds <c>Microsoft.CodeAnalysis.Text.Extensions.GetOpenDocumentInCurrentContextWithChanges(ITextSnapshot)</c>.</summary>
    private static Func<ITextSnapshot, Document?>? ResolveGetDocument()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.EditorFeatures.Text");
            var method = assembly?.GetType("Microsoft.CodeAnalysis.Text.Extensions")?.GetMethod(
                "GetOpenDocumentInCurrentContextWithChanges",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(ITextSnapshot)],
                modifiers: null);
            if (method is null || !typeof(Document).IsAssignableFrom(method.ReturnType))
            {
                return null;
            }

            return (Func<ITextSnapshot, Document?>)Delegate.CreateDelegate(typeof(Func<ITextSnapshot, Document?>), method);
        }
#pragma warning disable CA1031 // Without Roslyn the heuristic is used.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
