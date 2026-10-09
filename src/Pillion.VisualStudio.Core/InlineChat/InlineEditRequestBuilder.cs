using System;
using System.Collections.Generic;
using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>
/// The document an inline chat session is about, captured when the prompt opens: its path, language, the text as it
/// was then (an immutable snapshot) and the scope. The buffer itself is never touched until the proposal is applied.
/// </summary>
public sealed class InlineChatDocument
{
    /// <summary>Initializes a new instance of the <see cref="InlineChatDocument"/> class.</summary>
    /// <param name="filePath">The document's full path.</param>
    /// <param name="languageId">The language identifier (see <see cref="DocumentLanguage"/>).</param>
    /// <param name="text">The document text when the prompt opened.</param>
    /// <param name="scope">The scope.</param>
    public InlineChatDocument(string filePath, string languageId, IDocumentText text, InlineScope scope)
    {
        FilePath = filePath ?? string.Empty;
        LanguageId = string.IsNullOrEmpty(languageId) ? DocumentLanguage.PlainText : languageId;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (scope.End > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        ScopeText = text.GetText(scope.Start, scope.Length);
    }

    /// <summary>Gets the document's full path.</summary>
    public string FilePath { get; }

    /// <summary>Gets the language identifier.</summary>
    public string LanguageId { get; }

    /// <summary>Gets the document text when the prompt opened.</summary>
    public IDocumentText Text { get; }

    /// <summary>Gets the scope.</summary>
    public InlineScope Scope { get; }

    /// <summary>Gets the scope's text, as it was when the prompt opened.</summary>
    public string ScopeText { get; }
}

/// <summary>Builds the <see cref="InlineEditRequest"/> for a prompt.</summary>
public static class InlineEditRequestBuilder
{
    /// <summary>The most characters of context sent before and after the scope (the host uses up to 6,000 of each).</summary>
    public const int MaxContextCharacters = 8000;

    /// <summary>Builds a request.</summary>
    /// <param name="document">The document and scope.</param>
    /// <param name="prompt">The parsed prompt.</param>
    /// <param name="requestId">The request id.</param>
    /// <param name="conversationId">The session's conversation id (the same for every prompt of the session).</param>
    /// <param name="modelId">The picked model (<c>providerId/modelId</c>), or <see langword="null"/> for Auto.</param>
    /// <param name="previousProposal">The proposal being refined, or <see langword="null"/> for a first prompt.</param>
    /// <param name="diagnostics">Diagnostics in the document; only those on the scope's lines are attached.</param>
    /// <returns>The request, with <see cref="Profile.Auto"/> set explicitly.</returns>
    public static InlineEditRequest Build(
        InlineChatDocument document,
        ParsedPrompt prompt,
        string requestId,
        string? conversationId,
        string? modelId,
        string? previousProposal,
        IEnumerable<InlineEditDiagnostic>? diagnostics)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var scope = document.Scope;
        var text = document.Text;
        return new InlineEditRequest
        {
            RequestId = requestId ?? string.Empty,
            FilePath = document.FilePath,
            LanguageId = document.LanguageId,
            ScopeText = document.ScopeText,
            ScopeStartLine = scope.StartLine,
            ScopeStartColumn = scope.Start - text.GetLineStart(scope.StartLine),
            TextBefore = Before(text, scope.Start),
            TextAfter = After(text, scope.End),
            Instruction = prompt.Instruction,
            Command = prompt.Command,
            Diagnostics = (diagnostics ?? [])
                .Where(d => d is not null && scope.ContainsLine(d.Line))
                .OrderBy(d => d.Line)
                .ToList(),
            PreviousProposal = previousProposal,
            ModelId = string.IsNullOrEmpty(modelId) ? null : modelId,

            // Explicit: the enum's default value is Fast, and Auto routes by the InlineEdit task class.
            Profile = Profile.Auto,
            ConversationId = conversationId,
        };
    }

    /// <summary>The text before the scope, at most <see cref="MaxContextCharacters"/>, starting at a line start.</summary>
    private static string Before(IDocumentText text, int start)
    {
        var from = Math.Max(0, start - MaxContextCharacters);
        if (from > 0)
        {
            var line = text.GetLineNumber(from);
            from = line + 1 < text.LineCount ? Math.Min(start, text.GetLineStart(line + 1)) : start;
        }

        return text.GetText(from, start - from);
    }

    /// <summary>The text after the scope, at most <see cref="MaxContextCharacters"/>, ending at a line end.</summary>
    private static string After(IDocumentText text, int end)
    {
        var to = Math.Min(text.Length, end + MaxContextCharacters);
        if (to < text.Length)
        {
            var line = text.GetLineNumber(to);
            to = Math.Max(end, text.GetLineStart(line));
        }

        return text.GetText(end, to - end);
    }
}
