using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Follows the proposals the suggestion service displays in one view (its <c>ProposalDisplayed</c> event, raised on the
/// UI thread), so a test waits for Pillion's proposal to be shown instead of polling the broker's state, and can say
/// what did happen when it is not. Created and disposed on the UI thread. Like <see cref="AutocompleteChecks"/>, use it
/// only after the package has loaded.
/// </summary>
public sealed class ProposalWatch : IDisposable
{
    private readonly object _gate = new();
    private readonly SuggestionServiceBase _service;
    private readonly ITextView _view;
    private readonly List<string> _displayed = [];
    private TaskCompletionSource<string>? _nextPillion;
    private int _pillionShown;

    private ProposalWatch(SuggestionServiceBase service, ITextView view)
    {
        _service = service;
        _view = view;
        _service.ProposalDisplayed += OnProposalDisplayed;
    }

    /// <summary>Gets the number of Pillion proposals displayed since the watch started.</summary>
    public int PillionShown => Volatile.Read(ref _pillionShown);

    /// <summary>Starts watching a view; returns <see langword="null"/> when Visual Studio has no suggestion service.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The watch.</returns>
    public static ProposalWatch? Start(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return AutocompleteChecks.SuggestionService() is { } service ? new ProposalWatch(service, view) : null;
    }

    /// <summary>
    /// Gets a task that completes with the id of the next Pillion proposal displayed after this call. Ask for it before the
    /// keys that should lead to the proposal, not after, or the display can be missed.
    /// </summary>
    /// <returns>The task.</returns>
    public Task<string> WhenNextPillionShownAsync()
    {
        lock (_gate)
        {
#pragma warning disable VSTHRD003 // Completed by the suggestion service's event on the UI thread; nothing awaits it while holding a lock or needing that thread.
            return (_nextPillion ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
#pragma warning restore VSTHRD003
        }
    }

    /// <summary>Lists what was displayed, for a failure message.</summary>
    /// <returns>One line.</returns>
    public string Describe()
    {
        lock (_gate)
        {
            return _displayed.Count == 0
                ? "no proposal was displayed"
                : _displayed.Count.ToString(CultureInfo.InvariantCulture) + " proposal(s) displayed: " + string.Join(", ", _displayed);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _service.ProposalDisplayed -= OnProposalDisplayed;

    private void OnProposalDisplayed(object sender, ProposalDisplayedEventArgs e)
    {
        if (!ReferenceEquals(e.View, _view) || e.OriginalProposal is not { } proposal)
        {
            return;
        }

        var id = proposal.ProposalId ?? "<no id>";
        TaskCompletionSource<string>? waiting = null;
        lock (_gate)
        {
            _displayed.Add(id);
            if (PillionProposalId.IsPillion(id))
            {
                Interlocked.Increment(ref _pillionShown);
                waiting = _nextPillion;
                _nextPillion = null;
            }
        }

        _ = waiting?.TrySetResult(id);
    }
}
