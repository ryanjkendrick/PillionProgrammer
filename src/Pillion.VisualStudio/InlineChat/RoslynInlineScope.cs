using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Pillion.Contracts;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>
/// Inline chat's use of the Roslyn workspace, for C# and Visual Basic: the member that encloses the caret (the inline
/// chat scope when nothing is selected) and the compiler diagnostics on the scope's lines (attached for <c>/fix</c> when the IDE signals service is not available).
/// Roslyn is supplied by Visual Studio at run time; the extension compiles against Microsoft.CodeAnalysis.Workspaces.Common
/// without shipping it, and finds the one editor entry point, <c>GetOpenDocumentInCurrentContextWithChanges</c>, by
/// reflection (as the autocomplete pipeline does). When anything is missing the callers fall back: the scope becomes
/// the caret's line and <c>/fix</c> goes without diagnostics.
/// </summary>
internal static class RoslynInlineScope
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static Func<ITextSnapshot, Document?>? _getDocument;
    private static long _nextResolveTicks;

    /// <summary>Gets a value indicating whether a language is served by Roslyn.</summary>
    /// <param name="languageId">The language identifier.</param>
    /// <returns><see langword="true"/> for C# and Visual Basic.</returns>
    public static bool Supports(string languageId) => languageId is "csharp" or "vb";

    /// <summary>
    /// Finds the member declaration that encloses a position: a method, constructor, property, indexer, event, operator
    /// or field (local functions, lambdas and accessors count as their member), with its doc comment lines. Not a type:
    /// between members there is no member, and the caller uses the caret's line.
    /// </summary>
    /// <param name="snapshot">The snapshot the position is in.</param>
    /// <param name="position">The position.</param>
    /// <param name="cancellationToken">A token that cancels the lookup.</param>
    /// <returns>The member's start and end in <paramref name="snapshot"/>, or <see langword="null"/>.</returns>
    public static async Task<(int Start, int End)?> FindMemberAsync(ITextSnapshot snapshot, int position, CancellationToken cancellationToken)
    {
        if (GetDocument is not { } getDocument || getDocument(snapshot) is not { } document)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var symbol = model.GetEnclosingSymbol(position, cancellationToken);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } or ILocalSymbol)
        {
            symbol = symbol.ContainingSymbol;
        }

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated })
        {
            symbol = associated; // An accessor: its property or event.
        }

        if (symbol is null or INamespaceOrTypeSymbol)
        {
            return null;
        }

        var tree = model.SyntaxTree;
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var node = symbol.DeclaringSyntaxReferences
            .Where(r => r.SyntaxTree == tree)
            .Select(r => r.GetSyntax(cancellationToken))
            .FirstOrDefault(n => n.FullSpan.Contains(position));
        if (node is null)
        {
            return null;
        }

        // Climb to the type's direct member: a field's declarator to its declaration, a Visual Basic method statement to
        // its block. Stop below the node that holds the containing type's declaration.
        var typeStart = symbol.ContainingType?.DeclaringSyntaxReferences
            .Where(r => r.SyntaxTree == tree)
            .Select(r => r.Span)
            .Where(s => s.Start <= node.SpanStart)
            .OrderByDescending(s => s.Start)
            .Select(s => (int?)s.Start)
            .FirstOrDefault();
        while (node.Parent is { } parent
            && parent != root
            && !(typeStart is { } start && parent.SpanStart <= start && start < parent.Span.End))
        {
            node = parent;
        }

        if (typeStart is { } type && node.SpanStart <= type && type < node.Span.End)
        {
            return null; // The "member" is the type itself (for example a record's primary constructor).
        }

        var memberStart = WithDocComment(snapshot, node.SpanStart);
        return (memberStart, node.Span.End);
    }

    /// <summary>Reads the compiler diagnostics (syntax and semantic, warnings and errors) on a span's lines.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="start">The span's start.</param>
    /// <param name="end">The span's end.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The diagnostics, or an empty list when Roslyn has no document for the snapshot.</returns>
    public static async Task<IReadOnlyList<InlineEditDiagnostic>> GetDiagnosticsAsync(ITextSnapshot snapshot, int start, int end, CancellationToken cancellationToken)
    {
        if (GetDocument is not { } getDocument || getDocument(snapshot) is not { } document)
        {
            return [];
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
        {
            return [];
        }

        var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var startLine = text.Lines.GetLineFromPosition(Math.Min(start, text.Length)).LineNumber;
        var endLine = text.Lines.GetLineFromPosition(Math.Min(end, text.Length)).LineNumber;
        var span = TextSpan.FromBounds(text.Lines[startLine].Start, text.Lines[endLine].End);
        return tree.GetDiagnostics(cancellationToken)
            .Where(d => d.Location.SourceSpan.IntersectsWith(span))
            .Concat(model.GetDiagnostics(span, cancellationToken))
            .Where(d => d.Severity is RoslynSeverity.Warning or RoslynSeverity.Error && d.Location.IsInSource)
            .GroupBy(d => (d.Id, d.Location.SourceSpan))
            .Select(g => g.First())
            .Select(d => new InlineEditDiagnostic
            {
                Line = text.Lines.GetLineFromPosition(d.Location.SourceSpan.Start).LineNumber,
                Severity = d.Severity == RoslynSeverity.Error ? Contracts.DiagnosticSeverity.Error : Contracts.DiagnosticSeverity.Warning,
                Code = d.Id,
                Message = d.GetMessage(CultureInfo.CurrentCulture),
            })
            .OrderBy(d => d.Line)
            .Take(50)
            .ToList();
    }

    /// <summary>Moves a member's start up over the doc comment lines (<c>///</c> or <c>'''</c>) directly above it.</summary>
    private static int WithDocComment(ITextSnapshot snapshot, int start)
    {
        var line = snapshot.GetLineFromPosition(Math.Min(start, snapshot.Length));
        var first = line;
        while (first.LineNumber > 0)
        {
            var above = snapshot.GetLineFromLineNumber(first.LineNumber - 1);
            var trimmed = above.GetText().TrimStart();
            if (!trimmed.StartsWith("///", StringComparison.Ordinal) && !trimmed.StartsWith("'''", StringComparison.Ordinal))
            {
                break;
            }

            first = above;
        }

        return first.LineNumber == line.LineNumber ? start : first.Start.Position;
    }

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

            var found = Resolve();
            Volatile.Write(ref _getDocument, found);
            return found;
        }
    }

    /// <summary>Finds <c>Microsoft.CodeAnalysis.Text.Extensions.GetOpenDocumentInCurrentContextWithChanges(ITextSnapshot)</c>.</summary>
    private static Func<ITextSnapshot, Document?>? Resolve()
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
#pragma warning disable CA1031 // Without Roslyn the scope is the caret's line and /fix goes without diagnostics.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
