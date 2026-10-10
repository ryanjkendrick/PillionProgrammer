using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>Adds <see cref="InlineChatCommandFilter"/> to every editable document view.</summary>
[Export(typeof(IVsTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class InlineChatCommandFilterProvider : IVsTextViewCreationListener
{
    private readonly IVsEditorAdaptersFactoryService _adapters;

    /// <summary>Initializes a new instance of the <see cref="InlineChatCommandFilterProvider"/> class.</summary>
    /// <param name="adapters">Maps the legacy text view to the WPF view.</param>
    [ImportingConstructor]
    public InlineChatCommandFilterProvider(IVsEditorAdaptersFactoryService adapters)
    {
        _adapters = adapters;
    }

    /// <inheritdoc/>
    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = _adapters.GetWpfTextView(textViewAdapter);
        if (view is null || !InlineChatController.IsDocumentView(view))
        {
            return;
        }

        var filter = new InlineChatCommandFilter(view);
        if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out var next)))
        {
            filter.Next = next;
        }
    }
}

/// <summary>
/// Keeps the editor's keys out of the document while keyboard focus is in the inline chat adornment. Visual Studio turns
/// many keys into editor commands (typing, Backspace, Delete, arrows, Home, End, Enter, Esc, Tab, Ctrl+A, Ctrl+C, Ctrl+V,
/// Ctrl+X, Ctrl+Z) and sends them to the active document view before WPF sees them, so without this filter they would
/// edit the code instead of the prompt. While the adornment has focus they are applied to the focused control instead;
/// Enter, Esc, Up, Down and Tab go to <see cref="InlineChatController.HandleKey"/>. Every other command, and every command
/// while focus is in the document, passes through unchanged.
/// </summary>
internal sealed class InlineChatCommandFilter : IOleCommandTarget
{
    private readonly IWpfTextView _view;

    /// <summary>Initializes a new instance of the <see cref="InlineChatCommandFilter"/> class.</summary>
    /// <param name="view">The document view.</param>
    public InlineChatCommandFilter(IWpfTextView view)
    {
        _view = view;
    }

    /// <summary>Gets or sets the next command target in the chain.</summary>
    public IOleCommandTarget? Next { get; set; }

    /// <inheritdoc/>
    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (InlineChatController.TryGet(_view, out var controller) && controller.OwnsKeyboardFocus && Handle(controller, pguidCmdGroup, nCmdID, pvaIn))
        {
            return VSConstants.S_OK;
        }

        return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    /// <inheritdoc/>
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (cCmds == 1 && InlineChatController.TryGet(_view, out var controller) && controller.OwnsKeyboardFocus && IsRouted(pguidCmdGroup, prgCmds[0].cmdID))
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    private static bool IsRouted(Guid group, uint id)
    {
        if (group == VSConstants.VSStd2K)
        {
            return (VSConstants.VSStd2KCmdID)id is VSConstants.VSStd2KCmdID.TYPECHAR or VSConstants.VSStd2KCmdID.BACKSPACE
                or VSConstants.VSStd2KCmdID.DELETE or VSConstants.VSStd2KCmdID.RETURN or VSConstants.VSStd2KCmdID.CANCEL
                or VSConstants.VSStd2KCmdID.TAB or VSConstants.VSStd2KCmdID.BACKTAB
                or VSConstants.VSStd2KCmdID.LEFT or VSConstants.VSStd2KCmdID.LEFT_EXT or VSConstants.VSStd2KCmdID.RIGHT or VSConstants.VSStd2KCmdID.RIGHT_EXT
                or VSConstants.VSStd2KCmdID.UP or VSConstants.VSStd2KCmdID.DOWN
                or VSConstants.VSStd2KCmdID.HOME or VSConstants.VSStd2KCmdID.HOME_EXT or VSConstants.VSStd2KCmdID.END or VSConstants.VSStd2KCmdID.END_EXT
                or VSConstants.VSStd2KCmdID.BOL or VSConstants.VSStd2KCmdID.BOL_EXT or VSConstants.VSStd2KCmdID.EOL or VSConstants.VSStd2KCmdID.EOL_EXT
                or VSConstants.VSStd2KCmdID.WORDPREV or VSConstants.VSStd2KCmdID.WORDPREV_EXT or VSConstants.VSStd2KCmdID.WORDNEXT or VSConstants.VSStd2KCmdID.WORDNEXT_EXT
                or VSConstants.VSStd2KCmdID.DELETEWORDLEFT or VSConstants.VSStd2KCmdID.DELETEWORDRIGHT
                or VSConstants.VSStd2KCmdID.SELECTALL or VSConstants.VSStd2KCmdID.UNDO or VSConstants.VSStd2KCmdID.REDO;
        }

        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            return (VSConstants.VSStd97CmdID)id is VSConstants.VSStd97CmdID.Copy or VSConstants.VSStd97CmdID.Cut
                or VSConstants.VSStd97CmdID.Paste or VSConstants.VSStd97CmdID.SelectAll or VSConstants.VSStd97CmdID.Delete
                or VSConstants.VSStd97CmdID.Undo or VSConstants.VSStd97CmdID.Redo;
        }

        return false;
    }

    private static bool Handle(InlineChatController controller, Guid group, uint id, IntPtr pvaIn)
    {
        if (!IsRouted(group, id))
        {
            return false;
        }

        var focused = Keyboard.FocusedElement as IInputElement;
        var textBox = focused as TextBoxBase;
        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            switch ((VSConstants.VSStd97CmdID)id)
            {
                case VSConstants.VSStd97CmdID.Copy:
                    return Run(ApplicationCommands.Copy, focused);
                case VSConstants.VSStd97CmdID.Cut:
                    return Run(ApplicationCommands.Cut, focused);
                case VSConstants.VSStd97CmdID.Paste:
                    return Run(ApplicationCommands.Paste, focused);
                case VSConstants.VSStd97CmdID.SelectAll:
                    return Run(ApplicationCommands.SelectAll, focused);
                case VSConstants.VSStd97CmdID.Delete:
                    return Run(EditingCommands.Delete, focused);
                case VSConstants.VSStd97CmdID.Undo:
                    return Run(ApplicationCommands.Undo, focused);
                case VSConstants.VSStd97CmdID.Redo:
                    return Run(ApplicationCommands.Redo, focused);
                default:
                    return false;
            }
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch ((VSConstants.VSStd2KCmdID)id)
        {
            case VSConstants.VSStd2KCmdID.TYPECHAR:
                var c = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn)!;
                if (textBox is TextBox { IsReadOnly: false } box)
                {
                    var caret = box.SelectionStart;
                    box.SelectedText = c.ToString();
                    box.Select(caret + 1, 0);
                }
                else if (c == ' ' && focused is ButtonBase)
                {
                    return controller.HandleKey(Key.Enter, shift: false);
                }

                return true;

            case VSConstants.VSStd2KCmdID.RETURN:
                return controller.HandleKey(Key.Enter, shift);
            case VSConstants.VSStd2KCmdID.CANCEL:
                return controller.HandleKey(Key.Escape, shift);
            case VSConstants.VSStd2KCmdID.UP:
            case VSConstants.VSStd2KCmdID.DOWN:
                // Used up even when nothing moves: the document's caret must not move behind the adornment.
                _ = controller.HandleKey((VSConstants.VSStd2KCmdID)id == VSConstants.VSStd2KCmdID.UP ? Key.Up : Key.Down, shift);
                return true;
            case VSConstants.VSStd2KCmdID.TAB:
                return controller.HandleKey(Key.Tab, shift: false);
            case VSConstants.VSStd2KCmdID.BACKTAB:
                return controller.HandleKey(Key.Tab, shift: true);
            case VSConstants.VSStd2KCmdID.BACKSPACE:
                return Run(EditingCommands.Backspace, focused);
            case VSConstants.VSStd2KCmdID.DELETE:
                return Run(EditingCommands.Delete, focused);
            case VSConstants.VSStd2KCmdID.DELETEWORDLEFT:
                return Run(EditingCommands.DeletePreviousWord, focused);
            case VSConstants.VSStd2KCmdID.DELETEWORDRIGHT:
                return Run(EditingCommands.DeleteNextWord, focused);
            case VSConstants.VSStd2KCmdID.LEFT:
                return Run(EditingCommands.MoveLeftByCharacter, focused);
            case VSConstants.VSStd2KCmdID.LEFT_EXT:
                return Run(EditingCommands.SelectLeftByCharacter, focused);
            case VSConstants.VSStd2KCmdID.RIGHT:
                return Run(EditingCommands.MoveRightByCharacter, focused);
            case VSConstants.VSStd2KCmdID.RIGHT_EXT:
                return Run(EditingCommands.SelectRightByCharacter, focused);
            case VSConstants.VSStd2KCmdID.WORDPREV:
                return Run(EditingCommands.MoveLeftByWord, focused);
            case VSConstants.VSStd2KCmdID.WORDPREV_EXT:
                return Run(EditingCommands.SelectLeftByWord, focused);
            case VSConstants.VSStd2KCmdID.WORDNEXT:
                return Run(EditingCommands.MoveRightByWord, focused);
            case VSConstants.VSStd2KCmdID.WORDNEXT_EXT:
                return Run(EditingCommands.SelectRightByWord, focused);
            case VSConstants.VSStd2KCmdID.HOME:
            case VSConstants.VSStd2KCmdID.BOL:
                return Run(EditingCommands.MoveToLineStart, focused);
            case VSConstants.VSStd2KCmdID.HOME_EXT:
            case VSConstants.VSStd2KCmdID.BOL_EXT:
                return Run(EditingCommands.SelectToLineStart, focused);
            case VSConstants.VSStd2KCmdID.END:
            case VSConstants.VSStd2KCmdID.EOL:
                return Run(EditingCommands.MoveToLineEnd, focused);
            case VSConstants.VSStd2KCmdID.END_EXT:
            case VSConstants.VSStd2KCmdID.EOL_EXT:
                return Run(EditingCommands.SelectToLineEnd, focused);
            case VSConstants.VSStd2KCmdID.SELECTALL:
                return Run(ApplicationCommands.SelectAll, focused);
            case VSConstants.VSStd2KCmdID.UNDO:
                return Run(ApplicationCommands.Undo, focused);
            case VSConstants.VSStd2KCmdID.REDO:
                return Run(ApplicationCommands.Redo, focused);
            default:
                return false;
        }
    }

    /// <summary>Runs a WPF command on the focused control; the key is used up either way, so the document never gets it.</summary>
    private static bool Run(RoutedCommand command, IInputElement? target)
    {
        if (target is not null && command.CanExecute(null, target))
        {
            command.Execute(null, target);
        }

        return true;
    }
}
