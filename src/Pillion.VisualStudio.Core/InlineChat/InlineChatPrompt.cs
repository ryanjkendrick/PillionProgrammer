using System;
using System.Collections.Generic;
using System.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>A slash shortcut the inline chat prompt offers as you type <c>/</c> (design section 5).</summary>
public sealed class SlashCommandInfo
{
    /// <summary>Initializes a new instance of the <see cref="SlashCommandInfo"/> class.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">The text typed, including the slash (for example <c>/doc</c>).</param>
    /// <param name="description">What it does, for the list and screen readers.</param>
    public SlashCommandInfo(InlineEditCommand command, string name, string description)
    {
        Command = command;
        Name = name;
        Description = description;
    }

    /// <summary>Gets the command.</summary>
    public InlineEditCommand Command { get; }

    /// <summary>Gets the text typed, including the slash.</summary>
    public string Name { get; }

    /// <summary>Gets what the command does.</summary>
    public string Description { get; }

    /// <summary>Gets the name a screen reader announces, for example "/doc: add documentation comments".</summary>
    public string AutomationName => Name + ": " + Description;

    /// <inheritdoc/>
    public override string ToString() => AutomationName;
}

/// <summary>What the user typed in the inline chat prompt, split into the slash shortcut and the instruction.</summary>
public readonly struct ParsedPrompt
{
    /// <summary>Initializes a new instance of the <see cref="ParsedPrompt"/> struct.</summary>
    /// <param name="command">The slash shortcut, or <see cref="InlineEditCommand.None"/>.</param>
    /// <param name="instruction">The rest of the text, trimmed.</param>
    public ParsedPrompt(InlineEditCommand command, string instruction)
    {
        Command = command;
        Instruction = instruction ?? string.Empty;
    }

    /// <summary>Gets the slash shortcut, or <see cref="InlineEditCommand.None"/>.</summary>
    public InlineEditCommand Command { get; }

    /// <summary>Gets the instruction without the shortcut (may be empty when a shortcut is given).</summary>
    public string Instruction { get; }

    /// <summary>Gets a value indicating whether there is anything to send: a shortcut or an instruction.</summary>
    public bool IsEmpty => Command == InlineEditCommand.None && Instruction.Length == 0;
}

/// <summary>Parses the inline chat prompt and offers slash-command completion.</summary>
public static class InlineChatPrompt
{
    /// <summary>The slash shortcuts, in the order the completion list shows them.</summary>
    public static IReadOnlyList<SlashCommandInfo> Commands { get; } =
    [
        new(InlineEditCommand.Doc, "/doc", "add documentation comments without changing the code"),
        new(InlineEditCommand.Test, "/test", "write tests for this code (inserted or copied, not replaced)"),
        new(InlineEditCommand.Fix, "/fix", "fix the errors and warnings reported on these lines"),
        new(InlineEditCommand.Explain, "/explain", "explain this code; nothing is changed"),
    ];

    /// <summary>
    /// Splits the prompt into a slash shortcut and the instruction. A shortcut is recognised only at the start, in any
    /// case, followed by white space or the end (<c>/doc</c>, <c>/FIX the null check</c>); anything else, including an
    /// unknown <c>/word</c>, is an instruction.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    /// <returns>The parsed prompt.</returns>
    public static ParsedPrompt Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length > 1 && trimmed[0] == '/')
        {
            var end = 1;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
            {
                end++;
            }

            var word = trimmed.Substring(0, end);
            var match = Commands.FirstOrDefault(c => string.Equals(c.Name, word, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new ParsedPrompt(match.Command, trimmed.Substring(end).Trim());
            }
        }

        return new ParsedPrompt(InlineEditCommand.None, trimmed);
    }

    /// <summary>
    /// Gets the shortcuts to offer while the prompt is a slash and the start of a name: <c>/</c> offers all of them,
    /// <c>/d</c> offers <c>/doc</c>. Nothing is offered once the text has a space or is a complete name.
    /// </summary>
    /// <param name="text">What the user typed so far.</param>
    /// <returns>The matching shortcuts (empty when none should be shown).</returns>
    public static IReadOnlyList<SlashCommandInfo> Suggest(string? text)
    {
        var value = (text ?? string.Empty).TrimStart();
        if (value.Length == 0 || value[0] != '/' || value.Any(char.IsWhiteSpace))
        {
            return [];
        }

        var matches = Commands.Where(c => c.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 && string.Equals(matches[0].Name, value, StringComparison.OrdinalIgnoreCase) ? [] : matches;
    }

    /// <summary>Gets the slash name of a command (for example <c>/doc</c>), or an empty string for <see cref="InlineEditCommand.None"/>.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The name.</returns>
    public static string NameOf(InlineEditCommand command) =>
        Commands.FirstOrDefault(c => c.Command == command)?.Name ?? string.Empty;
}
