using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Adds <see cref="AutocompleteCommandFilter"/> to every editable document view, ahead of the editor's own handlers.
/// </summary>
[Export(typeof(IVsTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class AutocompleteCommandFilterProvider : IVsTextViewCreationListener
{
    private readonly IVsEditorAdaptersFactoryService _adapters;
    private readonly AutocompleteHost _host;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteCommandFilterProvider"/> class.</summary>
    /// <param name="adapters">Maps the legacy text view to the WPF view.</param>
    /// <param name="host">The shared autocomplete host.</param>
    [ImportingConstructor]
    public AutocompleteCommandFilterProvider(IVsEditorAdaptersFactoryService adapters, AutocompleteHost host)
    {
        _adapters = adapters;
        _host = host;
    }

    /// <inheritdoc/>
    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = _adapters.GetWpfTextView(textViewAdapter);
        if (view is null || !AutocompleteHost.IsDocumentView(view))
        {
            return;
        }

        var filter = new AutocompleteCommandFilter(view, _host);
        if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out var next)))
        {
            filter.Next = next;
            _host.GetOrCreate(view).EditorCommands = next;
        }
    }
}

/// <summary>
/// The command filter on the text view for word-at-a-time accept and next and previous suggestion (design section 5,
/// Commands and default shortcuts). Ctrl+Right (<c>Edit.WordNext</c>, and on Visual Studio 18 <c>Edit.AcceptNextWordinSuggestion</c>)
/// accepts the next word only while a Pillion suggestion is displayed and moves the caret as usual otherwise. "Pillion: Next Suggestion" (Alt+]) and "Pillion:
/// Previous Suggestion" (Alt+[) are commands in the package's command set, routed here because the active view's
/// filters see every command first; while a Pillion suggestion is displayed they run the editor's own
/// <c>Edit.NextSuggestion</c> and <c>Edit.PreviousSuggestion</c>, which cycle through the proposals Pillion returned.
/// "Pillion: Suggest" (the prefix chord, then S) asks for one suggestion at the caret (<see cref="ViewAutocomplete.SuggestNow"/>).
/// Tab and Esc are left to the editor: Pillion never takes Tab, whoever's proposal is displayed.
/// </summary>
internal sealed class AutocompleteCommandFilter : IOleCommandTarget
{
    private readonly IWpfTextView _view;
    private readonly AutocompleteHost _host;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteCommandFilter"/> class.</summary>
    /// <param name="view">The view.</param>
    /// <param name="host">The shared autocomplete host.</param>
    public AutocompleteCommandFilter(IWpfTextView view, AutocompleteHost host)
    {
        _view = view;
        _host = host;
    }

    /// <summary>
    /// The command set of Visual Studio 18's inline-suggestion commands; <c>Edit.AcceptNextWordinSuggestion</c> (id 100)
    /// is bound to Ctrl+Right in the "Inline Suggestions Active" scope and would otherwise take Pillion's suggestion.
    /// </summary>
    internal static readonly Guid InlineSuggestionCommands = new("160961B3-909D-4B28-9353-A1BEF587B4A6");

    private const uint AcceptNextWordInSuggestion = 100;

    /// <summary><c>Edit.PreviousSuggestion</c> (Alt+, by default) in <see cref="InlineSuggestionCommands"/>.</summary>
    private const uint PreviousSuggestion = 93;

    /// <summary><c>Edit.NextSuggestion</c> (Alt+. by default) in <see cref="InlineSuggestionCommands"/>.</summary>
    internal const uint NextSuggestion = 94;

    /// <summary>Gets or sets the next command target in the chain.</summary>
    public IOleCommandTarget? Next { get; set; }

    /// <inheritdoc/>
    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var timing = UiThreadTimings.Measure("autocomplete.filter.exec");
        if (AutocompleteHost.TryGet(_view, out var state))
        {
            if (IsWordAccept(pguidCmdGroup, nCmdID) && state.TryAcceptNextWord())
            {
                timing.Dispose();
                return VSConstants.S_OK;
            }

            if (pguidCmdGroup == PackageGuids.CommandSet && nCmdID == PackageIds.SuggestCommand)
            {
                _ = state.SuggestNow();
                timing.Dispose();
                return VSConstants.S_OK;
            }

            if (pguidCmdGroup == PackageGuids.CommandSet
                && nCmdID is PackageIds.NextSuggestionCommand or PackageIds.PreviousSuggestionCommand)
            {
                if (state.IsPillionSuggestionDisplayed() && Next is { } next)
                {
                    var group = InlineSuggestionCommands;
                    var id = nCmdID == PackageIds.NextSuggestionCommand ? NextSuggestion : PreviousSuggestion;
                    timing.Dispose();
                    _ = next.Exec(ref group, id, nCmdexecopt, pvaIn, pvaOut);
                    return VSConstants.S_OK;
                }

                timing.Dispose();
                return VSConstants.S_OK;
            }
        }

        timing.Dispose();
        return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    private static bool IsWordAccept(Guid group, uint id) =>
        (group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.WORDNEXT)
        || (group == InlineSuggestionCommands && id == AcceptNextWordInSuggestion);

    /// <inheritdoc/>
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (pguidCmdGroup == PackageGuids.CommandSet && cCmds == 1
            && prgCmds[0].cmdID is PackageIds.NextSuggestionCommand or PackageIds.PreviousSuggestionCommand or PackageIds.SuggestCommand)
        {
            var enabled = _host.Feature?.Availability.Settings.Enabled ?? false;
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | (enabled ? OLECMDF.OLECMDF_ENABLED : 0));
            return VSConstants.S_OK;
        }

        return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }
}
