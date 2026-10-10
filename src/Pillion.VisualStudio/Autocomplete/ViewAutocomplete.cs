using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Autocomplete for one document view: turns the broker's proposal requests into pipeline attempts and the results
/// into proposals, follows the suggestion service's events for its proposals and for other providers', records edits,
/// implements word accept, next and previous suggestion and "Pillion: Suggest" for the command filter, and applies the
/// ordering setting and the coexistence countermeasure (<see cref="CoexistenceTimeline"/>, work item 1.4).
/// </summary>
internal sealed class ViewAutocomplete
{
    private readonly AutocompleteHost _host;
    private readonly ITextView _view;
    private readonly object _gate = new();
    private AutocompleteSession? _session;
    private AutocompleteFeature? _feature;
    private Shown? _shown;
    private long _announcedAttempt;
    private volatile bool _displayed;
    private int _acceptPending;
    private (int Version, SuppressionReason Reason, AutocompleteSettings Settings)? _suppression;
    private bool _closed;
    /// <summary>How long a push waits after the decision, so it never runs inside the suggestion service's own event handling.</summary>
    private static readonly TimeSpan PushSettle = TimeSpan.FromMilliseconds(150);

    private Kept? _kept;
    private CaretKey? _retriedAt;
    private long _following;
    private int _textChangePosted;

    /// <summary>Initializes a new instance of the <see cref="ViewAutocomplete"/> class.</summary>
    /// <param name="host">The shared autocomplete host.</param>
    /// <param name="view">The document view.</param>
    /// <param name="filePath">The document's path, or an empty string.</param>
    public ViewAutocomplete(AutocompleteHost host, ITextView view, string filePath)
    {
        _host = host;
        _view = view;
        FilePath = filePath;
        LanguageId = string.IsNullOrEmpty(filePath)
            ? LanguageFromContentType(view.TextBuffer.ContentType.TypeName)
            : DocumentLanguage.FromPath(filePath);
        if (LanguageId == DocumentLanguage.PlainText)
        {
            var fromContentType = LanguageFromContentType(view.TextBuffer.ContentType.TypeName);
            LanguageId = fromContentType;
        }

        DocumentKey = string.IsNullOrEmpty(filePath)
            ? "buffer:" + RuntimeHelpers.GetHashCode(view.TextBuffer).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : filePath;

        Timeline = new CoexistenceTimeline(() => DateTimeOffset.UtcNow);
        view.TextBuffer.Changed += OnBufferChanged;
        view.Caret.PositionChanged += OnCaretMoved;
        view.GotAggregateFocus += OnFocus;
        view.Closed += OnClosed;
    }

    /// <summary>Gets the document's path, or an empty string for an unsaved buffer.</summary>
    public string FilePath { get; }

    /// <summary>Gets the language identifier.</summary>
    public string LanguageId { get; }

    /// <summary>Gets the key that identifies the document in the pipeline.</summary>
    public string DocumentKey { get; }

    /// <summary>Gets the coexistence state of the view (UI thread only).</summary>
    public CoexistenceTimeline Timeline { get; }

    /// <summary>
    /// Gets or sets the editor's command handlers after Pillion's command filter, used to make the editor ask every
    /// proposal source again (<c>Edit.NextSuggestion</c> with nothing displayed is an explicit invocation).
    /// </summary>
    public IOleCommandTarget? EditorCommands { get; set; }

    /// <summary>
    /// Answers the proposal manager on the UI thread, so it must be trivial: whether Pillion wants to propose here at
    /// all (feature on, document not suppressed). The completion-list rule is applied per request.
    /// </summary>
    /// <param name="caret">The caret.</param>
    /// <returns><see langword="true"/> when Pillion's source should be asked.</returns>
    public bool WantsProposals(VirtualSnapshotPoint caret)
    {
        var feature = _host.Feature;
        if (feature is null
            || _closed
            || caret.Position.Snapshot.TextBuffer != _view.TextBuffer
            || DocumentSuppression(caret.Position.Snapshot, feature.Engine.Settings) != SuppressionReason.None)
        {
            return false;
        }

        var proposes = CoexistencePolicy.ManagerProposes(feature.Engine.Settings.Ordering, CompetitorPresent(feature), IsKeptFor(KeyOf(caret.Position)));
        if (CoexistenceTrace.IsOn && (_kept is not null || !proposes))
        {
            CoexistenceTrace.Write("manager", "at=" + KeyOf(caret.Position) + " kept=" + (_kept?.Key.ToString() ?? "none") + " proposes=" + proposes);
        }

        return proposes;
    }

    /// <summary>Handles a proposal request from the broker (on a thread-pool thread).</summary>
    /// <param name="caret">The caret.</param>
    /// <param name="completionState">The completion list state, or <see langword="null"/> when no list is open.</param>
    /// <param name="scenario">What triggered the request.</param>
    /// <param name="cancellationToken">The broker's token, cancelled when the request is superseded.</param>
    /// <returns>The proposals, or <see langword="null"/>.</returns>
    public async Task<ProposalCollectionBase?> RequestAsync(
        VirtualSnapshotPoint caret,
        CompletionState? completionState,
        ProposalScenario scenario,
        CancellationToken cancellationToken)
    {
        var feature = _host.Feature;
        if (feature is null || _closed)
        {
            return null;
        }

        feature.Log.Debug(
            "autocomplete.requested",
            "The proposal broker asked Pillion's source for a suggestion.",
            ("scenario", scenario.ToString()),
            ("completionList", completionState is not null),
            ("ordering", feature.Engine.Settings.Ordering.ToString()));
        var session = EnsureSession(feature);
        var snapshot = caret.Position.Snapshot;
        var suppression = snapshot.TextBuffer != _view.TextBuffer
            ? SuppressionReason.None
            : SuppressionRules.IsCompletionListSelection(completionState?.SelectedItem)
                ? SuppressionReason.CompletionListSelection
                : DocumentSuppression(snapshot, feature.Engine.Settings);
        if (snapshot.TextBuffer != _view.TextBuffer || suppression != SuppressionReason.None)
        {
            session.CancelPending();
            if (suppression != SuppressionReason.None)
            {
                feature.Engine.Metrics.Suppressed(LanguageId);
                feature.Log.Debug("autocomplete.suppressed", "No suggestion requested.", ("reason", suppression.ToString()), ("scenario", scenario.ToString()));
            }

            return null;
        }

        var text = new SnapshotDocumentText(snapshot);
        var newLine = NewLineAt(caret.Position);
        var key = KeyOf(caret.Position);
        var kept = TakeKept(key);
        var answer = CoexistencePolicy.Source(feature.Engine.Settings.Ordering, CompetitorPresent(feature), kept is not null);
        if (CoexistenceTrace.IsOn && scenario == ProposalScenario.ExplicitInvocation)
        {
            CoexistenceTrace.Write("source.explicit", "at=" + key + " answer=" + answer);
        }
        if (answer == SourceAnswer.FromKept && kept is not null && ProposalFactory.CreateInsertion(caret, kept.Text, PillionProposalId.Create(kept.Attempt, 0)) is { } keptProposal)
        {
            session.CancelPending();
            lock (_gate)
            {
                _shown = new Shown(kept.Attempt, caret, kept.Text, isMultiLine: kept.Text.IndexOf('\n') >= 0);
            }

            CoexistenceTrace.Write("source.kept", "attempt=" + kept.Attempt + " at=" + key + " scenario=" + scenario);
            return ProposalFactory.Collection(keptProposal);
        }

        if (answer is SourceAnswer.Nothing or SourceAnswer.FromKept)
        {
            session.CancelPending();
            return null;
        }

        // Typing through, or after a word accept: offer the rest of the remembered suggestion at once.
        if (answer == SourceAnswer.Request && !caret.IsInVirtualSpace && session.Continuations.TryGetRemainder(DocumentKey, text, caret.Position, out var rest, out var restAttempt))
        {
            session.CancelPending();
            var continued = ProposalFactory.CreateInsertion(caret, rest, PillionProposalId.Create(restAttempt, 0));
            if (continued is not null)
            {
                feature.Log.Debug("autocomplete.continued", "Offered the rest of the remembered suggestion.", ("attempt", restAttempt), ("chars", rest.Length), ("scenario", scenario.ToString()));
                lock (_gate)
                {
                    _shown = new Shown(restAttempt, caret, rest, isMultiLine: rest.IndexOf('\n') >= 0);
                }

                return ProposalFactory.Collection(continued);
            }
        }

        var immediate = Interlocked.Exchange(ref _acceptPending, 0) != 0 || scenario == ProposalScenario.CompletedProposal;
        var attempt = session.Start(
            new CompletionInput
            {
                DocumentKey = DocumentKey,
                FilePath = FilePath,
                LanguageId = LanguageId,
                Text = text,
                TextVersion = snapshot.Version.VersionNumber,
                Caret = caret.Position.Position,
                VirtualSpaces = caret.VirtualSpaces,
                Immediate = immediate,
                DeclarationProvider = RoslynDeclarationProvider.TryCreate(snapshot, caret.Position.Position, LanguageId),
            },
            answer == SourceAnswer.RequestInBackground ? CancellationToken.None : cancellationToken);

        if (answer == SourceAnswer.RequestInBackground)
        {
            // Competitor first: answer with nothing so the broker shows the competitor's proposal, and fill in later
            // only if nothing is displayed once the competitor had its chance.
            _ = FollowAsync(feature, attempt, key, newLine, DateTimeOffset.UtcNow + CoexistencePolicy.CompetitorGrace);
            return null;
        }

        #pragma warning disable VSTHRD003 // The attempt's tasks are completed by thread-pool work and never need the UI thread.
        var result = await attempt.Ready.ConfigureAwait(false);
        #pragma warning restore VSTHRD003
        if (result is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var documentText = ProposalFactory.ToDocumentLineBreaks(result.Text, newLine);
        var proposal = ProposalFactory.CreateInsertion(caret, documentText, PillionProposalId.Create(attempt.Id, 0));
        if (proposal is null)
        {
            feature.Log.Debug("autocomplete.proposal.invalid", "The editor rejected a proposal.", ("request", attempt.RequestId));
            return null;
        }

        var shown = new Shown(attempt.Id, caret, documentText, result.IsMultiLine);
        lock (_gate)
        {
            _shown = shown;
        }

        // Answered after the broker stopped waiting (about 0.8 s) and showed a competitor's proposal: ours is only
        // queued behind it, so take the slot back (Pillion first).
        _ = _host.JoinableTaskFactory.RunAsync(async () =>
        {
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Timeline.CompetitorDisplayed)
            {
                CoexistenceTrace.Write("pillion.late", "attempt=" + attempt.Id + " at=" + key);
                HandleReady(feature, key, documentText, attempt.Id);
            }
        });

        if (result.IsComplete)
        {
            Remember(session, shown, documentText);
        }
        else
        {
            _ = FollowUpAsync(feature, session, attempt, shown, newLine);
        }

        if (!result.IsComplete)
        {
            return ProposalFactory.Collection(proposal);
        }

        // The variants travel in the same collection, so the editor's own next and previous suggestion commands
        // (which Alt+] and Alt+[ forward to) cycle through them.
        var proposals = new List<ProposalBase> { proposal };
        var variants = shown.Variants;
        for (var i = 1; i < variants.Count; i++)
        {
            if (ProposalFactory.CreateInsertion(caret, variants[i], PillionProposalId.Create(attempt.Id, i)) is { } variant)
            {
                proposals.Add(variant);
            }
        }

        return ProposalFactory.Collection([.. proposals]);
    }

    /// <summary>Called when one of Pillion's proposals is displayed.</summary>
    /// <param name="proposal">The proposal.</param>
    public void OnDisplayed(ProposalBase proposal)
    {
        if (!PillionProposalId.TryParse(proposal.ProposalId, out var attempt, out _))
        {
            return;
        }

        _displayed = true;
        OnUiThread(() => Timeline.PillionShown(attempt));
        CoexistenceTrace.Write("pillion.shown", "attempt=" + attempt);
        var feature = _feature;
        feature?.Engine.Metrics.SuggestionShown(attempt);
        feature?.Log.Debug("autocomplete.shown", "Suggestion shown.", ("proposal", proposal.ProposalId));
        if (feature is null || !feature.Engine.Settings.AnnounceSuggestions || Interlocked.Exchange(ref _announcedAttempt, attempt) == attempt)
        {
            return;
        }

        if (ProposalFactory.TryGetInsertion(proposal, proposal.Caret.Position, out var text))
        {
            SuggestionAnnouncer.Announce(_host.JoinableTaskFactory, _view, text);
        }
    }

    /// <summary>Called when one of Pillion's proposals was updated or diverged from.</summary>
    /// <param name="original">The proposal as first displayed.</param>
    /// <param name="final">The proposal after the update, or <see langword="null"/>.</param>
    /// <param name="reason">Why.</param>
    public void OnRejected(ProposalBase original, ProposalBase? final, ReasonForUpdate reason)
    {
        if (((int)reason & (int)ReasonForUpdate.Diverged) != 0)
        {
            _displayed = false;
            if (PillionProposalId.TryParse(original.ProposalId, out var attempt, out _))
            {
                _feature?.Engine.Metrics.SuggestionRejected(attempt);
                var byUser = reason == ReasonForUpdate.DivergedDueToChangeProposal;
                OnUiThread(() => Timeline.PillionRejected(attempt, byUser));
                CoexistenceTrace.Write("pillion.rejected", "attempt=" + attempt + " reason=" + reason);
            }
        }

        _feature?.Log.Debug(
            "autocomplete.rejected",
            "Suggestion updated or diverged.",
            ("proposal", original.ProposalId),
            ("reason", reason.ToString()),
            ("final", final?.ProposalId));
    }

    /// <summary>Called when one of Pillion's proposals was accepted (Tab, or typed through).</summary>
    /// <param name="original">The proposal.</param>
    /// <param name="reason">How.</param>
    public void OnAccepted(ProposalBase original, ReasonForAccept reason)
    {
        _displayed = false;
        if (PillionProposalId.TryParse(original.ProposalId, out var attempt, out _))
        {
            _feature?.Engine.Metrics.SuggestionAccepted(attempt);
            OnUiThread(() => Timeline.PillionGone(attempt));
            CoexistenceTrace.Write("pillion.accepted", "attempt=" + attempt + " reason=" + reason);
        }

        _feature?.Log.Debug("autocomplete.accepted", "Suggestion accepted.", ("proposal", original.ProposalId), ("reason", reason.ToString()));
        Interlocked.Exchange(ref _acceptPending, 1);
        if (_session is { } session && original.Edits is { Count: > 0 } edits)
        {
            // Accepted at a line end: the next request (made at once) is multi-line.
            var start = edits[0].Span.Start;
            var line = start.GetContainingLine();
            session.PreviousAcceptedAtLineEnd = start.Snapshot.GetText(start.Position, line.End.Position - start.Position).Trim().Length == 0;
        }
    }

    /// <summary>Called when one of Pillion's proposals was dismissed.</summary>
    /// <param name="original">The proposal.</param>
    /// <param name="reason">Why.</param>
    public void OnDismissed(ProposalBase original, ReasonForDismiss reason)
    {
        _displayed = false;
        if (reason != ReasonForDismiss.DismissedForRefinement && PillionProposalId.TryParse(original.ProposalId, out var attempt, out _))
        {
            _feature?.Engine.Metrics.SuggestionRejected(attempt);
            OnUiThread(() => Timeline.PillionGone(attempt));
            CoexistenceTrace.Write("pillion.dismissed", "attempt=" + attempt + " reason=" + reason);
        }

        _feature?.Log.Debug("autocomplete.dismissed", "Suggestion dismissed.", ("proposal", original.ProposalId), ("reason", reason.ToString()));
        if (reason is ReasonForDismiss.DismissedAfterUserEscape or ReasonForDismiss.DismissedAfterViewClosed)
        {
            _session?.Continuations.Clear();
            lock (_gate)
            {
                _shown = null;
            }
        }
    }

    /// <summary>Called when another provider's proposal (Copilot's, IntelliCode's) is displayed in this view.</summary>
    /// <param name="proposal">The proposal.</param>
    public void OnCompetitorDisplayed(ProposalBase proposal) =>
        _host.JoinableTaskFactory.RunAsync(() => OnCompetitorDisplayedAsync(proposal)).FileAndForget("pillion/autocomplete/competitorshown");

    /// <summary>Called when another provider's displayed proposal was accepted, dismissed or diverged from.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="what">What happened, for the trace.</param>
    public void OnCompetitorGone(ProposalBase? proposal, string what)
    {
        OnUiThread(() => Timeline.CompetitorGone());
        CoexistenceTrace.Write("competitor.gone", what + " id=" + proposal?.ProposalId);
    }

    /// <summary>
    /// "Pillion: Suggest": asks for one suggestion at the caret now and shows it when it is ready, whatever the ordering
    /// and whatever else is displayed (the only way to get one under <see cref="AutocompleteOrdering.OnDemand"/>).
    /// </summary>
    /// <returns><see langword="true"/> when a request was started.</returns>
    public bool SuggestNow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_closed || _host.Feature is not { } feature)
        {
            return false;
        }

        var caret = _view.Caret.Position.VirtualBufferPosition;
        var suppression = DocumentSuppression(caret.Position.Snapshot, feature.Engine.Settings);
        if (suppression != SuppressionReason.None)
        {
            feature.Log.Info("autocomplete.suggest.suppressed", "Pillion: Suggest is not available here.", ("reason", suppression.ToString()));
            return false;
        }

        SyncTimeline(feature);
        var key = CurrentKey();
        var decision = Timeline.SuggestRequested(key);
        CoexistenceTrace.Write("suggest.requested", "at=" + key);
        Execute(feature, decision, key, immediate: true);
        return true;
    }

    /// <summary>
    /// Accepts the next word of the displayed Pillion suggestion (Ctrl+Right). The rest stays on offer: the word is
    /// inserted as one undoable edit, and the remembered suggestion supplies the remainder at once.
    /// </summary>
    /// <returns><see langword="true"/> when a Pillion suggestion was displayed and a word was accepted.</returns>
    public bool TryAcceptNextWord()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_session is not { } session || DisplayedPillionProposal() is not { } displayed)
        {
            return false;
        }

        var caret = _view.Caret.Position.VirtualBufferPosition;
        if (!PillionProposalId.TryParse(displayed.ProposalId, out var attempt, out _))
        {
            return false;
        }

        // After a word accept the editor may still report the proposal as first shown (a buffer edit is not typing),
        // so the remembered suggestion is the authority on what is left; the displayed proposal is the fallback.
        string remaining;
        if (!caret.IsInVirtualSpace
            && session.Continuations.TryGetRemainder(DocumentKey, new SnapshotDocumentText(caret.Position.Snapshot), caret.Position.Position, out var remembered, out var rememberedAttempt)
            && rememberedAttempt == attempt)
        {
            remaining = remembered;
        }
        else if (!ProposalFactory.TryGetInsertion(displayed, caret.Position, out remaining))
        {
            return false;
        }

        var padding = caret.IsInVirtualSpace ? new string(' ', caret.VirtualSpaces) : string.Empty;
        var word = remaining.Substring(0, WordBoundary.NextWordLength(remaining));
        var snapshot = caret.Position.Snapshot;
        session.Continuations.Remember(DocumentKey, new SnapshotDocumentText(snapshot), caret.Position.Position, padding + remaining, attempt);

        using (var edit = _view.TextBuffer.CreateEdit())
        {
            edit.Insert(caret.Position.Position, padding + word);
            edit.Apply();
        }

        _view.Caret.MoveTo(new SnapshotPoint(_view.TextSnapshot, caret.Position.Position + padding.Length + word.Length));
        _feature?.Engine.Metrics.WordAccepted(attempt);
        _feature?.Log.Debug("autocomplete.word_accepted", "Accepted the next word of a suggestion.", ("proposal", displayed.ProposalId), ("chars", word.Length));
        if (word.Length < remaining.Length)
        {
            ShowRemainder(attempt);
        }

        return true;
    }

    /// <summary>
    /// Gets a value indicating whether next and previous suggestion (Alt+] and Alt+[) apply: a Pillion suggestion is
    /// displayed. The command filter then forwards them to the editor's own next and previous suggestion commands,
    /// which cycle through the proposals of the displayed collection (the suggestion and, for a multi-line one, its
    /// first line). The suggestion service offers no way for a source to swap a displayed proposal itself.
    /// </summary>
    /// <returns><see langword="true"/> when a Pillion suggestion is displayed.</returns>
    public bool IsPillionSuggestionDisplayed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var displayed = DisplayedPillionProposal() is not null;
        if (displayed)
        {
            _feature?.Log.Debug("autocomplete.variant", "Next or previous suggestion.");
        }

        return displayed;
    }

    private async Task OnCompetitorDisplayedAsync(ProposalBase proposal)
    {
        // Leave the suggestion service's event handler first; everything below may dismiss or invoke commands.
        await Task.Yield();
        await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_closed || _host.Feature is not { } feature)
        {
            return;
        }

        var kind = CompetitorProposals.Classify(proposal.ProposalId);
        feature.Coexistence.CompetitorSeen(kind == CompetitorKind.None ? CompetitorKind.Other : kind, _host.JoinableTaskFactory);
        SyncTimeline(feature);
        var key = CurrentKey();
        if (IsKeptFor(key) && _retriedAt != key && _host.Suggestions is { } suggestions)
        {
            // A push's explicit invocation re-showed a competitor proposal the broker still had queued, without asking
            // any source (seen on 18.10 after the user pressed Esc). Clear the queue the only way that works and ask again once.
            _retriedAt = key;
            CoexistenceTrace.Write("push.retry", "id=" + proposal.ProposalId + " at=" + key);
            await Task.Delay(PushSettle).ConfigureAwait(false);
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            await suggestions.DismissProposalAsync(_view, ReasonForDismiss.DismissedAfterUserEscape, CancellationToken.None).ConfigureAwait(true);
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (CurrentKey() == key && IsKeptFor(key))
            {
                InvokeExplicitRequest(feature);
                return;
            }
        }

        var decision = Timeline.CompetitorShown(kind, key);
        CoexistenceTrace.Write("competitor.shown", "kind=" + kind + " id=" + proposal.ProposalId + " at=" + key + " displaced=" + decision.DisplacedAttempt + " actions=" + decision.Actions);
        Execute(feature, decision, key);
    }

    private async Task TextChangedAsync(AutocompleteFeature feature)
    {
        await Task.Yield();
        await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
        Interlocked.Exchange(ref _textChangePosted, 0);
        if (!_closed)
        {
            SyncTimeline(feature);
            var key = CurrentKey();
            Execute(feature, Timeline.TextChanged(key), key);
        }
    }

    private static CaretKey KeyOf(SnapshotPoint point) => new(point.Snapshot.Version.VersionNumber, point.Position);

    private static bool CompetitorPresent(AutocompleteFeature feature) => feature.Coexistence.CompetitorPresent;

    /// <summary>Runs a timeline update on the UI thread (the suggestion service raises its events there; this is a safety net).</summary>
    private void OnUiThread(Action action)
    {
        if (ThreadHelper.CheckAccess())
        {
            action();
            return;
        }

        _host.JoinableTaskFactory.RunAsync(async () =>
        {
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            action();
        }).FileAndForget("pillion/autocomplete/coexistence");
    }

    private CaretKey CurrentKey() => KeyOf(_view.Caret.Position.BufferPosition);

    private void SyncTimeline(AutocompleteFeature feature)
    {
        Timeline.Ordering = feature.Engine.Settings.Ordering;
        Timeline.Mode = feature.Coexistence.Mode;
    }

    private bool IsKeptFor(CaretKey key) => _kept is { } kept && kept.Key == key && DateTimeOffset.UtcNow <= kept.Until;

    private Kept? TakeKept(CaretKey key)
    {
        lock (_gate)
        {
            var kept = _kept;
            if (kept is null || kept.Key != key || DateTimeOffset.UtcNow > kept.Until)
            {
                return null;
            }

            _kept = null;
            return kept;
        }
    }

    /// <summary>Whether a completion list is open: Pillion never shows a proposal then (stricter than "an item is selected", which only the broker's requests can see).</summary>
    private bool IsCompletionActive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SComponentModel)) is not IComponentModel componentModel)
        {
            return false;
        }

        return componentModel.GetService<IAsyncCompletionBroker>()?.IsCompletionActive(_view) == true
            || componentModel.GetService<ICompletionBroker>()?.IsCompletionActive(_view) == true;
    }

    /// <summary>Carries out a coexistence decision on the UI thread.</summary>
    private void Execute(AutocompleteFeature feature, CoexistenceDecision decision, CaretKey key, bool immediate = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (decision.DisplacedAttempt != 0)
        {
            feature.Engine.Metrics.SuggestionDisplaced(decision.DisplacedAttempt);
            feature.Log.Debug("autocomplete.displaced", "Another provider's proposal replaced a Pillion suggestion.", ("attempt", decision.DisplacedAttempt));
        }

        if (decision.Has(CoexistenceActions.DismissCompetitor))
        {
            DismissDisplayed("decline");
        }

        if (decision.Has(CoexistenceActions.RequestPillion))
        {
            RequestOutsideBroker(feature, key, immediate);
        }

        if (decision.Has(CoexistenceActions.RequestAll))
        {
            InvokeExplicitRequest(feature);
        }
    }

    /// <summary>
    /// Asks for a Pillion suggestion at the caret without the broker: the type-through cache, the attempt already in
    /// flight for this caret, or a new attempt (with the normal debounce unless <paramref name="immediate"/>).
    /// </summary>
    private void RequestOutsideBroker(AutocompleteFeature feature, CaretKey key, bool immediate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var caret = _view.Caret.Position.VirtualBufferPosition;
        var snapshot = caret.Position.Snapshot;
        if (KeyOf(caret.Position) != key || (!immediate && IsCompletionActive()))
        {
            return;
        }

        var session = EnsureSession(feature);
        var text = new SnapshotDocumentText(snapshot);
        if (!caret.IsInVirtualSpace && session.Continuations.TryGetRemainder(DocumentKey, text, caret.Position, out var rest, out var restAttempt))
        {
            CoexistenceTrace.Write("pillion.cached", "attempt=" + restAttempt + " at=" + key);
            HandleReady(feature, key, rest, restAttempt);
            return;
        }

        var attempt = session.CurrentFor(snapshot.Version.VersionNumber, caret.Position.Position) ?? session.Start(
            new CompletionInput
            {
                DocumentKey = DocumentKey,
                FilePath = FilePath,
                LanguageId = LanguageId,
                Text = text,
                TextVersion = snapshot.Version.VersionNumber,
                Caret = caret.Position.Position,
                VirtualSpaces = caret.VirtualSpaces,
                Immediate = immediate,
                DeclarationProvider = RoslynDeclarationProvider.TryCreate(snapshot, caret.Position.Position, LanguageId),
            },
            CancellationToken.None);
        if (Interlocked.Exchange(ref _following, attempt.Id) == attempt.Id)
        {
            return;
        }

        CoexistenceTrace.Write("pillion.request", "attempt=" + attempt.Id + " at=" + key + " immediate=" + immediate);
        _ = FollowAsync(feature, attempt, key, NewLineAt(caret.Position), notBefore: null);
    }

    /// <summary>Waits for an attempt started outside the broker's wait and hands its suggestion to the timeline.</summary>
    private async Task FollowAsync(AutocompleteFeature feature, CompletionAttempt attempt, CaretKey key, string newLine, DateTimeOffset? notBefore)
    {
        try
        {
            #pragma warning disable VSTHRD003 // The attempt's tasks are completed by thread-pool work and never need the UI thread.
            var result = await attempt.Ready.ConfigureAwait(false);
            #pragma warning restore VSTHRD003
            if (notBefore is { } due && due > DateTimeOffset.UtcNow)
            {
                await Task.Delay(due - DateTimeOffset.UtcNow).ConfigureAwait(false);
            }

            var documentText = result is null ? null : ProposalFactory.ToDocumentLineBreaks(result.Text, newLine);
            var current = _view.TextBuffer.CurrentSnapshot;
            if (result is { IsComplete: true } && _session is { } session && documentText is not null && current.Version.VersionNumber == key.Version)
            {
                session.Continuations.Remember(DocumentKey, new SnapshotDocumentText(current), key.Position, documentText, attempt.Id);
            }

            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            CoexistenceTrace.Write("pillion.ready", "attempt=" + attempt.Id + " at=" + key + " chars=" + (documentText?.Length ?? 0));
            HandleReady(feature, key, documentText, attempt.Id);
        }
#pragma warning disable CA1031 // A failed follow-up only means the competitor's proposal stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            feature.Log.Debug("autocomplete.coexistence.follow.failed", "Could not follow a suggestion.", ("error", ex.GetType().Name));
        }
    }

    /// <summary>A Pillion suggestion is ready for a caret outside the broker's wait: ask the timeline whether to show it.</summary>
    private void HandleReady(AutocompleteFeature feature, CaretKey requestedAt, string? text, long attempt)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_closed)
        {
            return;
        }

        SyncTimeline(feature);
        var current = CurrentKey();
        var decision = Timeline.PillionReady(requestedAt, current, !string.IsNullOrEmpty(text) && text!.Trim().Length > 0);
        if (decision.Has(CoexistenceActions.PushPillion))
        {
            _ = PushAsync(feature, current, text!, attempt);
        }
        else if (decision.Has(CoexistenceActions.RequestAll))
        {
            _host.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(PushSettle + PushSettle).ConfigureAwait(false);
                await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_closed && CurrentKey() == current && DisplayedPillionProposal() is null)
                {
                    InvokeExplicitRequest(feature);
                }
            }).FileAndForget("pillion/autocomplete/coexistencerequestall");
        }
    }

    /// <summary>
    /// Shows a ready Pillion suggestion: keeps it for exactly this caret, dismisses what is displayed (with the
    /// user-Escape reason, the only one that clears the broker's queued proposals), and makes the editor ask every source
    /// again; Pillion's source then answers at once from what it kept, ahead of the others.
    /// </summary>
    private async Task PushAsync(AutocompleteFeature feature, CaretKey key, string text, long attempt)
    {
        try
        {
            // Never from inside the suggestion service's own display: dismissing and invoking re-entrantly, within a few
            // milliseconds of a competitor's proposal being shown, left IntelliCode's broker on 17.14 asking no source again.
            await Task.Delay(PushSettle).ConfigureAwait(false);
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_closed || CurrentKey() != key)
            {
                return;
            }

            if (IsCompletionActive() || DisplayedPillionProposal() is not null)
            {
                CoexistenceTrace.Write("push.skipped", "attempt=" + attempt + " completionList=" + IsCompletionActive());
                return;
            }

            lock (_gate)
            {
                _kept = new Kept(key, text, attempt, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
            }

            if (_host.Suggestions is { } suggestions && suggestions.GetDisplayedProposal(_view) is { } displayed)
            {
                CoexistenceTrace.Write("push.dismiss", "id=" + displayed.ProposalId);
                await suggestions.DismissProposalAsync(_view, ReasonForDismiss.DismissedAfterUserEscape, CancellationToken.None).ConfigureAwait(true);
                await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            }

            if (CurrentKey() != key)
            {
                return;
            }

            CoexistenceTrace.Write("push", "attempt=" + attempt + " at=" + key);
            feature.Log.Debug("autocomplete.coexistence.push", "Showing a Pillion suggestion in place of another provider's.", ("attempt", attempt));
            InvokeExplicitRequest(feature);
        }
#pragma warning disable CA1031 // A failed push leaves the slot as it was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            feature.Log.Debug("autocomplete.coexistence.push.failed", "Could not show a Pillion suggestion.", ("error", ex.GetType().Name));
        }
    }

    private void DismissDisplayed(string why)
    {
        if (_host.Suggestions is not { } suggestions)
        {
            return;
        }

        CoexistenceTrace.Write("dismiss", why);
        _host.JoinableTaskFactory.RunAsync(async () =>
        {
            await Task.Delay(PushSettle).ConfigureAwait(false);
            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            await suggestions.DismissProposalAsync(_view, ReasonForDismiss.DismissedAfterUserEscape, CancellationToken.None).ConfigureAwait(true);
        })
            .FileAndForget("pillion/autocomplete/coexistencedismiss");
    }

    /// <summary>Runs the editor's <c>Edit.NextSuggestion</c>: with nothing displayed, the broker asks every source (an explicit invocation).</summary>
    private void InvokeExplicitRequest(AutocompleteFeature feature)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (EditorCommands is not { } commands)
        {
            feature.Log.Debug("autocomplete.coexistence.nocommands", "No editor command target to ask the sources again.");
            return;
        }

        var group = AutocompleteCommandFilter.InlineSuggestionCommands;
        var hr = commands.Exec(ref group, AutocompleteCommandFilter.NextSuggestion, 0, IntPtr.Zero, IntPtr.Zero);
        CoexistenceTrace.Write("explicit", "hr=0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string LanguageFromContentType(string contentType) => contentType switch
    {
        "CSharp" => "csharp",
        "Basic" => "vb",
        "Python" => "python",
        "TypeScript" => "typescript",
        "JavaScript" => "javascript",
        "C/C++" => "cpp",
        "F#" => "fsharp",
        "XAML" => "xaml",
        "Razor" or "RazorCSharp" or "RazorCoreCSharp" => "razor",
        _ => DocumentLanguage.PlainText,
    };

    private static string NewLineAt(SnapshotPoint position)
    {
        var line = position.GetContainingLine();
        if (line.LineBreakLength > 0)
        {
            return line.GetLineBreakText();
        }

        return line.LineNumber > 0 && position.Snapshot.GetLineFromLineNumber(line.LineNumber - 1) is { LineBreakLength: > 0 } previous
            ? previous.GetLineBreakText()
            : Environment.NewLine;
    }

    private AutocompleteSession EnsureSession(AutocompleteFeature feature)
    {
        lock (_gate)
        {
            if (_session is null)
            {
                _feature = feature;
                _session = feature.Engine.CreateSession();
                feature.Engine.OpenDocuments.Opened(FilePath);
            }

            return _session;
        }
    }

    private SuppressionReason DocumentSuppression(ITextSnapshot snapshot, AutocompleteSettings settings)
    {
        var cached = _suppression;
        if (cached is { } entry && entry.Version == snapshot.Version.VersionNumber && ReferenceEquals(entry.Settings, settings))
        {
            return entry.Reason;
        }

        var reason = SuppressionRules.EvaluateDocument(FilePath, LanguageId, new SnapshotDocumentText(snapshot), settings);
        _suppression = (snapshot.Version.VersionNumber, reason, settings);
        return reason;
    }

    private ProposalBase? DisplayedPillionProposal()
    {
        var displayed = _host.Suggestions?.GetDisplayedProposal(_view);
        return PillionProposalId.IsPillion(displayed?.ProposalId) ? displayed : null;
    }

    private void Remember(AutocompleteSession session, Shown shown, string documentText)
    {
        var padding = shown.Caret.IsInVirtualSpace ? new string(' ', shown.Caret.VirtualSpaces) : string.Empty;
        session.Continuations.Remember(
            DocumentKey,
            new SnapshotDocumentText(shown.Caret.Position.Snapshot),
            shown.Caret.Position.Position,
            padding + documentText,
            shown.Attempt);
    }

    /// <summary>Waits for the whole of a multi-line suggestion that was shown early, then remembers and (if still untouched) extends it.</summary>
    private async Task FollowUpAsync(AutocompleteFeature feature, AutocompleteSession session, CompletionAttempt attempt, Shown shown, string newLine)
    {
        try
        {
            #pragma warning disable VSTHRD003 // The attempt's tasks are completed by thread-pool work and never need the UI thread.
            var final = await attempt.Completed.ConfigureAwait(false);
            #pragma warning restore VSTHRD003
            if (final is null)
            {
                return;
            }

            var documentText = ProposalFactory.ToDocumentLineBreaks(final.Text, newLine);
            shown.SetPrimary(documentText);
            Remember(session, shown, documentText);

            await _host.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_closed || _view.TextSnapshot != shown.Caret.Position.Snapshot)
            {
                return; // The user typed: the remembered suggestion supplies the rest on the next request.
            }

            var displayed = DisplayedPillionProposal();
            if (displayed?.ProposalId != PillionProposalId.Create(attempt.Id, 0))
            {
                return;
            }

            var proposal = ProposalFactory.CreateInsertion(shown.Caret, documentText, PillionProposalId.Create(attempt.Id, 0));
            var updated = proposal is not null ? _host.Suggestions?.TryUpdateProposal(_view, proposal) : null;
            feature.Log.Debug("autocomplete.extended", "Extended a suggestion shown early.", ("request", attempt.RequestId), ("updated", updated is not null));
        }
#pragma warning disable CA1031 // A follow-up failure only means the early part stays shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            feature.Log.Debug("autocomplete.extend.failed", "Could not extend a suggestion.", ("error", ex.GetType().Name));
        }
    }

    /// <summary>After a word accept, replaces the displayed suggestion with its rest at the new caret.</summary>
    private void ShowRemainder(long attempt)
    {
        var caret = _view.Caret.Position.VirtualBufferPosition;
        if (_session is not { } session)
        {
            return;
        }

        if (session.Continuations.TryGetRemainder(DocumentKey, new SnapshotDocumentText(caret.Position.Snapshot), caret.Position.Position, out var rest, out _)
            && ProposalFactory.CreateInsertion(caret, rest, PillionProposalId.Create(attempt, 0)) is { } proposal)
        {
            var updated = _host.Suggestions?.TryUpdateProposal(_view, proposal);
            _feature?.Log.Debug("autocomplete.remainder", "Re-showed the rest of a suggestion after a word accept.", ("updated", updated is not null));
        }
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (_session is not { } session || _feature is not { } feature)
        {
            return;
        }

        session.CancelPendingUnless(e.After.Version.VersionNumber, caret: null);
        if (Timeline.CompetitorDisplayed && Interlocked.Exchange(ref _textChangePosted, 1) == 0)
        {
            // The caret settles after this event; while a competitor's proposal is typed through the broker asks no source.
            _host.JoinableTaskFactory.RunAsync(() => TextChangedAsync(feature)).FileAndForget("pillion/autocomplete/coexistencetext");
        }
        else if (!Timeline.CompetitorDisplayed)
        {
            Timeline.TextChanged(KeyOf(_view.Caret.Position.BufferPosition));
        }

        if (e.Changes.Count > 0)
        {
            var line = e.After.GetLineFromPosition(e.Changes[0].NewPosition);
            var text = line.Length <= 400 ? line.GetText() : e.After.GetText(line.Start.Position, 400);
            feature.Engine.Edits.Record(string.IsNullOrEmpty(FilePath) ? DocumentKey : FilePath, line.LineNumber, text);
        }
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
    {
        _session?.CancelPendingUnless(e.NewPosition.BufferPosition.Snapshot.Version.VersionNumber, e.NewPosition.BufferPosition.Position);

        // Pillion's ghost text belongs at the caret. On Visual Studio 18, when the caret moves away without an edit
        // (arrow keys, a click), the editor keeps the proposal as a "go to suggestion" hint and asks no source for the
        // new position, so dismiss it. Visual Studio 2022 (IntelliCode's suggestion service) dismisses it by itself,
        // and dismissing there as well races with its request for the new position. Typing is left to reconciliation.
        if (_displayed
            && IntelliCodeCheck.VisualStudioMajorVersion >= 18
            && e.OldPosition.BufferPosition.Snapshot == e.NewPosition.BufferPosition.Snapshot
            && DisplayedPillionProposal() is { } displayed
            && !ProposalFactory.TryGetInsertion(displayed, e.NewPosition.BufferPosition, out _)
            && _host.Suggestions is { } suggestions)
        {
            _displayed = false;
            _host.JoinableTaskFactory.RunAsync(() => suggestions.DismissProposalAsync(_view, ReasonForDismiss.DismissedAfterCaretMoved, CancellationToken.None))
                .FileAndForget("pillion/autocomplete/dismissmoved");
        }
    }

    private void OnFocus(object sender, EventArgs e) => _feature?.Engine.OpenDocuments.Activated(FilePath);

    private void OnClosed(object sender, EventArgs e)
    {
        _closed = true;
        _view.TextBuffer.Changed -= OnBufferChanged;
        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.GotAggregateFocus -= OnFocus;
        _view.Closed -= OnClosed;
        _feature?.Engine.OpenDocuments.Closed(FilePath);
        _session?.Dispose();
    }

    /// <summary>A Pillion suggestion kept for the next request at exactly one caret (a push).</summary>
    private sealed class Kept(CaretKey key, string text, long attempt, DateTimeOffset until)
    {
        public CaretKey Key { get; } = key;

        public string Text { get; } = text;

        public long Attempt { get; } = attempt;

        public DateTimeOffset Until { get; } = until;
    }

    /// <summary>The suggestion last returned for this view and its variants (next and previous suggestion).</summary>
    private sealed class Shown
    {
        private readonly object _gate = new();
        private readonly List<string> _variants = [];

        public Shown(long attempt, VirtualSnapshotPoint caret, string text, bool isMultiLine)
        {
            Attempt = attempt;
            Caret = caret;
            IsMultiLine = isMultiLine;
            SetPrimary(text);
        }

        public long Attempt { get; }

        public VirtualSnapshotPoint Caret { get; }

        public bool IsMultiLine { get; }

        public IReadOnlyList<string> Variants
        {
            get
            {
                lock (_gate)
                {
                    return [.. _variants];
                }
            }
        }

        /// <summary>Sets the primary suggestion; a multi-line one also offers its first line as a variant.</summary>
        public void SetPrimary(string text)
        {
            lock (_gate)
            {
                _variants.Clear();
                _variants.Add(text);
                if (CompletionShaper.FirstLineVariant(text.Replace("\r\n", "\n")) is { } firstLine)
                {
                    var newLine = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
                    _variants.Add(ProposalFactory.ToDocumentLineBreaks(firstLine, newLine));
                }
            }
        }
    }
}
