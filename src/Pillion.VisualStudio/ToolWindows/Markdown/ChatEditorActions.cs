using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The Visual Studio side of the chat window's code and link actions: Insert at cursor into the active document, the
/// confirmation before a link opens in the default browser, and the confirmation before a conversation is deleted.
/// UI thread only.
/// </summary>
internal sealed class ChatEditorActions
{
    private readonly IServiceProvider _services;
    private readonly IVsTextManager? _textManager;
    private readonly IVsEditorAdaptersFactoryService? _adapters;
    private readonly ITextDocumentFactoryService? _documents;
    private readonly PillionLog? _log;

    /// <summary>Initializes a new instance of the <see cref="ChatEditorActions"/> class.</summary>
    /// <param name="services">The global service provider (for message boxes).</param>
    /// <param name="textManager">The text manager, which knows the last active editor.</param>
    /// <param name="adapters">The editor adapter factory.</param>
    /// <param name="documents">The text document factory (for the inserted-into file name).</param>
    /// <param name="log">The extension log.</param>
    public ChatEditorActions(
        IServiceProvider services,
        IVsTextManager? textManager,
        IVsEditorAdaptersFactoryService? adapters,
        ITextDocumentFactoryService? documents,
        PillionLog? log)
    {
        _services = services;
        _textManager = textManager;
        _adapters = adapters;
        _documents = documents;
        _log = log;
    }

    /// <summary>Gets a value indicating whether Insert at cursor can work (the editor services were found).</summary>
    public bool CanInsert => _textManager is not null && _adapters is not null;

    /// <summary>
    /// Inserts code into the active document at the caret, replacing the selection if there is one, as one edit (one undo
    /// step). Line breaks follow the document's, and lines after the first take the caret line's indentation when the
    /// caret is in the leading whitespace.
    /// </summary>
    /// <param name="code">The code, with <c>\n</c> line breaks.</param>
    /// <returns>The note to show beside the button.</returns>
    public string InsertAtCaret(string code)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_textManager is null || _adapters is null
            || ErrorHandler.Failed(_textManager.GetActiveView(0, null, out var vsView))
            || vsView is null
            || _adapters.GetWpfTextView(vsView) is not { IsClosed: false } view)
        {
            return ChatStrings.InsertNoDocument;
        }

        if (view.Options.GetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId))
        {
            return ChatStrings.InsertReadOnly;
        }

        var snapshot = view.TextBuffer.CurrentSnapshot;
        var span = view.Selection.IsEmpty || view.Selection.SelectedSpans.Count == 0
            ? new Span(view.Caret.Position.BufferPosition.TranslateTo(snapshot, PointTrackingMode.Positive).Position, 0)
            : view.Selection.SelectedSpans[0].TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive).Span;
        if (view.TextBuffer.IsReadOnly(span))
        {
            return ChatStrings.InsertReadOnly;
        }

        var text = Prepare(code, snapshot, span.Start);
        try
        {
            using var edit = view.TextBuffer.CreateEdit();
            edit.Replace(span, text);
            var after = edit.Apply();
            if (edit.Canceled)
            {
                return ChatStrings.InsertReadOnly;
            }

            view.Selection.Clear();
            view.Caret.MoveTo(new SnapshotPoint(after, Math.Min(after.Length, span.Start + text.Length)));
            view.Caret.EnsureVisible();
        }
        catch (InvalidOperationException ex)
        {
            _log?.Warning("chat.insert.failed", "Insert at cursor failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            return ChatStrings.InsertReadOnly;
        }

        var name = _documents is not null && _documents.TryGetTextDocument(view.TextBuffer, out var document)
            ? Path.GetFileName(document.FilePath)
            : null;
        return ChatStrings.Format(ChatStrings.CodeInsertedFormat, string.IsNullOrEmpty(name) ? ChatStrings.ActiveDocumentName : name);
    }

    /// <summary>Asks whether to open a link, showing its address, and opens it in the default browser on OK.</summary>
    /// <param name="uri">A link that <see cref="LinkSafety"/> allowed.</param>
    public void ConfirmAndOpen(Uri uri)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!LinkSafety.TryGetSafeUri(uri?.OriginalString, out var safe) || safe is null)
        {
            return;
        }

        var answer = VsShellUtilities.ShowMessageBox(
            _services,
            ChatStrings.Format(ChatStrings.OpenLinkConfirmFormat, LinkSafety.Describe(safe)),
            ChatStrings.OpenLinkTitle,
            OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND); // Cancel is the default.
        if (answer != (int)VSConstants.MessageBoxResult.IDOK)
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _log?.Warning("chat.link.failed", "Opening a link failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    /// <summary>Asks before a conversation with messages is deleted.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns><see langword="true"/> to delete it.</returns>
    public bool ConfirmDelete(ChatConversation conversation)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var answer = VsShellUtilities.ShowMessageBox(
            _services,
            ChatStrings.Format(ChatStrings.DeleteConversationConfirmFormat, conversation.Title),
            ChatStrings.DeleteConversationName,
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
        return answer == (int)VSConstants.MessageBoxResult.IDOK;
    }

    /// <summary>Converts the code's line breaks to the document's and indents continuation lines to the caret's indentation.</summary>
    private static string Prepare(string code, ITextSnapshot snapshot, int position)
    {
        var newLine = snapshot.LineCount > 1 ? snapshot.GetLineFromLineNumber(0).GetLineBreakText() : Environment.NewLine;
        if (string.IsNullOrEmpty(newLine))
        {
            newLine = Environment.NewLine;
        }

        var line = snapshot.GetLineFromPosition(position);
        var before = snapshot.GetText(line.Start, position - line.Start);
        var indent = before.Trim().Length == 0 ? before : string.Empty;

        var lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(newLine);
                if (lines[i].Length > 0)
                {
                    builder.Append(indent);
                }
            }

            builder.Append(lines[i]);
        }

        return builder.ToString();
    }
}
