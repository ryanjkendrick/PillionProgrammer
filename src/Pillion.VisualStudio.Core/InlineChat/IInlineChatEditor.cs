using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>
/// What an <see cref="InlineChatSession"/> needs from the editor: write the result into the document, copy it, and close
/// the adornment. Implemented by the extension over the text view; every member is called on the UI thread.
/// </summary>
public interface IInlineChatEditor
{
    /// <summary>
    /// Replaces the scope with the proposal as one undoable edit, after asking source control to check the file out.
    /// </summary>
    /// <param name="expectedScopeText">The scope's text when the prompt opened; the edit is refused if it changed since.</param>
    /// <param name="proposal">The replacement.</param>
    /// <returns><see langword="null"/> when the edit was applied; otherwise why not, for the user.</returns>
    string? ReplaceScope(string expectedScopeText, string proposal);

    /// <summary>Inserts text at the caret as one undoable edit, after asking source control to check the file out.</summary>
    /// <param name="text">The text (for <c>/test</c>, the new test code).</param>
    /// <returns><see langword="null"/> when the text was inserted; otherwise why not, for the user.</returns>
    string? InsertAtCaret(string text);

    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="text">The text.</param>
    void Copy(string text);

    /// <summary>Closes the adornment and gives keyboard focus back to the editor. The session is disposed afterwards.</summary>
    void Close();
}

/// <summary>
/// Replaces the host's inline edit service for every new inline chat session. Meant for the Visual Studio integration
/// tests, which run inside devenv.exe and have no model; <see langword="null"/> (the default) uses Pillion Host.
/// </summary>
public static class InlineChatOverrides
{
    private static volatile IInlineEditService? _editService;

    /// <summary>Gets or sets the inline edit service new sessions use instead of the host's, or <see langword="null"/>.</summary>
    public static IInlineEditService? EditService
    {
        get => _editService;
        set => _editService = value;
    }
}
