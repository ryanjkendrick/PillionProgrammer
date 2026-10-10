using System.Globalization;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Editing;

/// <summary>What an inline edit prompt is built from. All text has LF line endings.</summary>
/// <param name="LanguageId">The language id (for example <c>csharp</c>).</param>
/// <param name="FileName">The document's file name (the name only; the folder is not sent).</param>
/// <param name="Command">The slash command.</param>
/// <param name="Instruction">What the user typed, possibly empty.</param>
/// <param name="Code">The code to edit: the scope, or the proposal being refined.</param>
/// <param name="FirstLine">The one-based document line the code starts on.</param>
/// <param name="IsRefinement">Whether <paramref name="Code"/> is an earlier proposal the model is to change.</param>
/// <param name="Before">The document text before the scope.</param>
/// <param name="After">The document text after the scope.</param>
/// <param name="Diagnostics">The diagnostics on the scope.</param>
/// <param name="History">Earlier instructions in the same inline session, oldest first.</param>
/// <param name="WholeScope">
/// Whether the model is asked to reply with the whole code rewritten as plain code instead of edit blocks (ignored for
/// <c>/test</c> and <c>/explain</c>).
/// </param>
public sealed record InlineEditPromptInput(
    string LanguageId,
    string FileName,
    InlineEditCommand Command,
    string Instruction,
    string Code,
    int FirstLine,
    bool IsRefinement,
    string Before,
    string After,
    IReadOnlyList<InlineEditDiagnostic> Diagnostics,
    IReadOnlyList<string> History,
    bool WholeScope = false);

/// <summary>
/// Builds the messages of an inline edit request from <see cref="InlineEditTemplates"/>: a system prompt for the
/// command, then one user message with, from most to least stable, the language and file, the context before, the code
/// to edit with line numbers, the context after, the diagnostics, earlier instructions, the task and the instruction.
/// It also renders the corrective messages for replies that cannot be used.
/// </summary>
public sealed class InlineEditPromptBuilder
{
    /// <summary>The default number of characters of context kept on each side of the scope.</summary>
    public const int DefaultContextCharacters = 6000;

    private readonly InlineEditTemplates _templates;
    private readonly int _contextCharacters;

    /// <summary>Initializes a new instance of the <see cref="InlineEditPromptBuilder"/> class.</summary>
    /// <param name="templates">The templates; <see cref="InlineEditTemplates.Default"/> when <see langword="null"/>.</param>
    /// <param name="contextCharacters">The most characters of context kept on each side of the scope (whole lines nearest the scope).</param>
    public InlineEditPromptBuilder(InlineEditTemplates? templates = null, int contextCharacters = DefaultContextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextCharacters);
        _templates = templates ?? InlineEditTemplates.Default;
        _contextCharacters = contextCharacters;
    }

    /// <summary>Gets the templates.</summary>
    public InlineEditTemplates Templates => _templates;

    /// <summary>Builds the system and user messages.</summary>
    /// <param name="input">The prompt input.</param>
    /// <returns>The messages, system first.</returns>
    public IReadOnlyList<ChatMessage> Build(InlineEditPromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var language = _templates.LanguageName(input.LanguageId);
        var tag = FenceTag(input.LanguageId);
        var sections = new List<string>
        {
            _templates.Section("header", ("language", language), ("file", string.IsNullOrEmpty(input.FileName) ? "(unsaved)" : input.FileName)),
        };

        var before = KeepEnd(input.Before, _contextCharacters);
        if (before.Trim().Length > 0)
        {
            sections.Add(Fenced("before", before, tag));
        }

        var numbered = EditText.NumberLines(input.Code, input.FirstLine);
        var lineCount = Math.Max(1, numbered.Split('\n').Length);
        sections.Add(Fenced(
            input.IsRefinement ? "proposal" : "scope",
            numbered,
            tag,
            ("first", Number(input.FirstLine)),
            ("last", Number(input.FirstLine + lineCount - 1))));

        var after = KeepStart(input.After, _contextCharacters);
        if (after.Trim().Length > 0)
        {
            sections.Add(Fenced("after", after, tag));
        }

        if (input.Diagnostics.Count > 0)
        {
            sections.Add(_templates.Section("diagnostics", ("items", FormatDiagnostics(input.Diagnostics))));
        }

        if (input.History.Count > 0)
        {
            sections.Add(_templates.Section("history", ("items", string.Join('\n', input.History.Select((h, i) => $"{i + 1}. {h}")))));
        }

        sections.Add(_templates.Section("task", ("task", _templates.Task(input.Command, input.LanguageId))));
        if (input.Instruction.Trim().Length > 0)
        {
            sections.Add(_templates.Section("instruction", ("instruction", input.Instruction.Trim())));
        }

        var whole = input.WholeScope && input.Command is not (InlineEditCommand.Test or InlineEditCommand.Explain);
        if (whole)
        {
            sections.Add(_templates.Section(input.IsRefinement ? "wholeRevised" : "wholeReply"));
        }

        return
        [
            new ChatMessage { Role = ChatRole.System, Content = _templates.SystemPrompt(input.Command, language, whole) },
            new ChatMessage { Role = ChatRole.User, Content = string.Join("\n\n", sections) },
        ];
    }

    /// <summary>The corrective message for blocks that did not match exactly once.</summary>
    /// <param name="failures">The failures.</param>
    /// <returns>The message.</returns>
    public string Unmatched(IEnumerable<EditBlockFailure> failures) =>
        _templates.Correction("unmatched", ("items", Bullets(failures.Select(f => f.Describe()))));

    /// <summary>The corrective message for blocks that could not be read.</summary>
    /// <param name="malformed">The malformed blocks.</param>
    /// <returns>The message.</returns>
    public string Malformed(IEnumerable<MalformedEditBlock> malformed) =>
        _templates.Correction("malformed", ("items", Bullets(malformed.Select(m => $"Block {m.Index}: {m.Reason}."))));

    /// <summary>The corrective message for a reply with code but no edit blocks.</summary>
    /// <returns>The message.</returns>
    public string NoBlocks() => _templates.Correction("noBlocks");

    /// <summary>The corrective message for a <c>/doc</c> edit that changed code.</summary>
    /// <param name="changes">The first changed code lines, quoted.</param>
    /// <param name="wholeScope">Whether the reply was a whole-scope rewrite rather than edit blocks.</param>
    /// <returns>The message.</returns>
    public string DocOnly(IEnumerable<string> changes, bool wholeScope = false) =>
        _templates.Correction(wholeScope ? "docOnlyWhole" : "docOnly", ("items", string.Join("; ", changes)));

    /// <summary>The corrective message for a whole-scope reply without code.</summary>
    /// <returns>The message.</returns>
    public string NoWholeCode() => _templates.Correction("noWholeCode");

    /// <summary>The corrective message for a <c>/test</c> reply without a code block.</summary>
    /// <returns>The message.</returns>
    public string NoCode() => _templates.Correction("noCode");

    /// <summary>The corrective message for an edit that introduced syntax errors.</summary>
    /// <param name="errors">The new errors, already worded with document line numbers.</param>
    /// <param name="proposal">The code with the edit applied (LF).</param>
    /// <param name="firstLine">The one-based document line the code starts on.</param>
    /// <param name="languageId">The language id.</param>
    /// <param name="wholeScope">Whether the reply was a whole-scope rewrite rather than edit blocks.</param>
    /// <returns>The message.</returns>
    public string Syntax(IEnumerable<string> errors, string proposal, int firstLine, string languageId, bool wholeScope = false)
    {
        var numbered = EditText.NumberLines(proposal, firstLine);
        var fence = EditText.Fence(numbered);
        return _templates.Correction(
            wholeScope ? "syntaxWhole" : "syntax",
            ("items", Bullets(errors)),
            ("fence", fence),
            ("tag", FenceTag(languageId)),
            ("text", numbered));
    }

    /// <summary>The corrective message for an edit that introduced placeholders no secret stands behind.</summary>
    /// <param name="placeholders">The unresolvable placeholders.</param>
    /// <param name="wholeScope">Whether the reply was a whole-scope rewrite rather than edit blocks.</param>
    /// <returns>The message.</returns>
    public string Placeholders(IEnumerable<string> placeholders, bool wholeScope = false) =>
        _templates.Correction(wholeScope ? "placeholdersWhole" : "placeholders", ("items", string.Join(", ", placeholders)));

    /// <summary>Keeps at most <paramref name="max"/> characters from the end of the text, starting at a line boundary.</summary>
    /// <param name="text">The text.</param>
    /// <param name="max">The most characters to keep.</param>
    /// <returns>The kept text.</returns>
    internal static string KeepEnd(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var start = text.Length - max;
        var newline = text.IndexOf('\n', start);
        return newline < 0 ? text[start..] : text[(newline + 1)..];
    }

    /// <summary>Keeps at most <paramref name="max"/> characters from the start of the text, ending at a line boundary.</summary>
    /// <param name="text">The text.</param>
    /// <param name="max">The most characters to keep.</param>
    /// <returns>The kept text.</returns>
    internal static string KeepStart(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var newline = text.LastIndexOf('\n', max);
        return newline <= 0 ? text[..max] : text[..newline];
    }

    private static string FenceTag(string languageId) =>
        string.IsNullOrWhiteSpace(languageId) || string.Equals(languageId, "plaintext", StringComparison.OrdinalIgnoreCase) ? string.Empty : languageId;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bullets(IEnumerable<string> items) => string.Join('\n', items.Select(i => "- " + i));

    private static string FormatDiagnostics(IEnumerable<InlineEditDiagnostic> diagnostics) =>
        Bullets(diagnostics.Select(d =>
        {
            var code = string.IsNullOrWhiteSpace(d.Code) ? string.Empty : " " + d.Code;
            return string.Create(CultureInfo.InvariantCulture, $"line {d.Line + 1}: {d.Severity.ToString().ToLowerInvariant()}{code}: {d.Message}");
        }));

    private string Fenced(string section, string text, string tag, params (string Name, string Value)[] extra)
    {
        var trimmed = text.TrimEnd('\n');
        var values = new List<(string, string)> { ("fence", EditText.Fence(trimmed)), ("tag", tag), ("text", trimmed) };
        values.AddRange(extra);
        return _templates.Section(section, [.. values]);
    }
}
