using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// The MEF side of autocomplete, shared by the proposal source, the proposal manager and the command filter: it finds
/// the package's <see cref="AutocompleteEngine"/> (loading the package in the background the first time an editor
/// asks), keeps one <see cref="ViewAutocomplete"/> per text view, and turns the suggestion service's shown, accepted
/// and rejected events into calls on the view they belong to, matched by Pillion's proposal ids.
/// </summary>
[Export(typeof(AutocompleteHost))]
internal sealed class AutocompleteHost
{
    private readonly ITextDocumentFactoryService _documents;
    private int _packageRequested;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteHost"/> class.</summary>
    /// <param name="documents">Maps buffers to their files.</param>
    /// <param name="joinableTaskContext">Visual Studio's joinable task context.</param>
    /// <param name="suggestions">The suggestion service, absent on Visual Studio 2022 without IntelliCode.</param>
    [ImportingConstructor]
    public AutocompleteHost(
        ITextDocumentFactoryService documents,
        JoinableTaskContext joinableTaskContext,
        [Import(AllowDefault = true)] SuggestionServiceBase? suggestions)
    {
        _documents = documents;
        JoinableTaskFactory = joinableTaskContext.Factory;
        Suggestions = suggestions;
        if (suggestions is not null)
        {
            suggestions.ProposalDisplayed += OnProposalDisplayed;
            suggestions.ProposalRejected += OnProposalRejected;
            suggestions.SuggestionAccepted += OnSuggestionAccepted;
            suggestions.SuggestionDismissed += OnSuggestionDismissed;
        }
    }

    /// <summary>Gets the joinable task factory for UI-thread work started by the MEF parts.</summary>
    public JoinableTaskFactory JoinableTaskFactory { get; }

    /// <summary>Gets the suggestion service, or <see langword="null"/> when Visual Studio has none (2022 without IntelliCode).</summary>
    public SuggestionServiceBase? Suggestions { get; }

    /// <summary>Gets the package's autocomplete feature, or <see langword="null"/> while the package is not loaded.</summary>
    public AutocompleteFeature? Feature
    {
        get
        {
            var feature = PillionPackage.Instance?.Autocomplete;
            if (feature is null)
            {
                EnsurePackageLoaded();
            }

            return feature;
        }
    }

    /// <summary>Gets (creating on first use) the autocomplete state of a document view.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The state.</returns>
    public ViewAutocomplete GetOrCreate(ITextView view) =>
        view.Properties.GetOrCreateSingletonProperty(typeof(ViewAutocomplete), () => new ViewAutocomplete(this, view, FilePathOf(view.TextBuffer)));

    /// <summary>Gets the autocomplete state of a view, if it has one.</summary>
    /// <param name="view">The view.</param>
    /// <param name="state">The state.</param>
    /// <returns><see langword="true"/> when the view has autocomplete state.</returns>
    public static bool TryGet(ITextView? view, out ViewAutocomplete state)
    {
        state = null!;
        return view is not null && view.Properties.TryGetProperty(typeof(ViewAutocomplete), out state);
    }

    /// <summary>Gets a value indicating whether a view is one Pillion offers suggestions in: an editable document view.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> for document views; the proposal metadata cannot say this, because the broker ignores text view roles.</returns>
    public static bool IsDocumentView(ITextView view) =>
        view.Roles.Contains(PredefinedTextViewRoles.Document)
        && view.Roles.Contains(PredefinedTextViewRoles.Editable)
        && !view.Roles.Contains(PredefinedTextViewRoles.EmbeddedPeekTextView);

    private string FilePathOf(ITextBuffer buffer) =>
        _documents.TryGetTextDocument(buffer, out var document) ? document.FilePath ?? string.Empty : string.Empty;

    /// <summary>Loads the package in the background (it owns the host connection); asking for its service is enough.</summary>
    private void EnsurePackageLoaded()
    {
        if (Interlocked.Exchange(ref _packageRequested, 1) != 0)
        {
            return;
        }

        JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(IHostConnection)).ConfigureAwait(false) is null)
                {
                    Interlocked.Exchange(ref _packageRequested, 0);
                }
            }
#pragma warning disable CA1031 // A failed load leaves autocomplete off; the next editor request tries again.
            catch (Exception)
#pragma warning restore CA1031
            {
                Interlocked.Exchange(ref _packageRequested, 0);
            }
        }).FileAndForget("pillion/autocomplete/loadpackage");
    }

    private void OnProposalDisplayed(object sender, ProposalDisplayedEventArgs e)
    {
        if (PillionProposalId.IsPillion(e.OriginalProposal?.ProposalId) && TryGet(e.View, out var state))
        {
            state.OnDisplayed(e.OriginalProposal!);
        }
    }

    private void OnProposalRejected(object sender, ProposalRejectedEventArgs e)
    {
        if (PillionProposalId.IsPillion(e.OriginalProposal?.ProposalId) && TryGet(e.View, out var state))
        {
            state.OnRejected(e.OriginalProposal!, e.FinalProposal, e.Reason);
        }
    }

    private void OnSuggestionAccepted(object sender, SuggestionAcceptedEventArgs e)
    {
        if (PillionProposalId.IsPillion(e.OriginalProposal?.ProposalId) && TryGet(e.View, out var state))
        {
            state.OnAccepted(e.OriginalProposal!, e.Reason);
        }
    }

    private void OnSuggestionDismissed(object sender, SuggestionDismissedEventArgs e)
    {
        if (PillionProposalId.IsPillion(e.OriginalProposal?.ProposalId) && TryGet(e.View, out var state))
        {
            state.OnDismissed(e.OriginalProposal!, e.Reason);
        }
    }
}
