using System;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>How Pillion answers a competitor's proposal taking the ghost-text slot under <see cref="AutocompleteOrdering.PillionFirst"/>.</summary>
public enum CountermeasureMode
{
    /// <summary>Do nothing: the competitor's proposal stays until it diverges (the behaviour before work item 1.4).</summary>
    Off,

    /// <summary>
    /// The default: keep the competitor's proposal on screen while Pillion asks for its own at the same caret (or finds
    /// one in its type-through cache), and replace it only when Pillion has one. The competitor is never left with nothing
    /// because Pillion had nothing.
    /// </summary>
    Replace,

    /// <summary>
    /// Evaluated and not chosen: dismiss the competitor's proposal at once while Pillion asks for its own, then show
    /// Pillion's, or ask every source again when Pillion has nothing.
    /// </summary>
    Decline,
}

/// <summary>A caret position in one text version.</summary>
public readonly struct CaretKey : IEquatable<CaretKey>
{
    /// <summary>Initializes a new instance of the <see cref="CaretKey"/> struct.</summary>
    /// <param name="version">The text snapshot version number.</param>
    /// <param name="position">The caret position (buffer offset).</param>
    public CaretKey(long version, int position)
    {
        Version = version;
        Position = position;
    }

    /// <summary>Gets the text snapshot version number.</summary>
    public long Version { get; }

    /// <summary>Gets the caret position (buffer offset).</summary>
    public int Position { get; }

    /// <summary>Compares two keys.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(CaretKey left, CaretKey right) => left.Equals(right);

    /// <summary>Compares two keys.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(CaretKey left, CaretKey right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(CaretKey other) => Version == other.Version && Position == other.Position;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CaretKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => unchecked((Version.GetHashCode() * 397) ^ Position);

    /// <inheritdoc/>
    public override string ToString() =>
        Version.ToString(CultureInfo.InvariantCulture) + "@" + Position.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the editor adapter does next.</summary>
[Flags]
public enum CoexistenceActions
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>
    /// Ask for a Pillion suggestion at the caret now, outside the broker (which asks no source while a displayed
    /// proposal still matches the text): the type-through cache first, then the pipeline.
    /// </summary>
    RequestPillion = 1,

    /// <summary>
    /// Show the Pillion suggestion that is ready for the caret: keep it for the next request at exactly this caret,
    /// dismiss whatever is displayed, and make the editor ask every source again (an explicit invocation), which
    /// Pillion's source answers at once from what it kept.
    /// </summary>
    PushPillion = 2,

    /// <summary>Dismiss the displayed competitor proposal now (<see cref="CountermeasureMode.Decline"/> only).</summary>
    DismissCompetitor = 4,

    /// <summary>Make the editor ask every source again, with nothing kept for Pillion (after a decline that found nothing).</summary>
    RequestAll = 8,
}

/// <summary>A decision of <see cref="CoexistenceTimeline"/>.</summary>
public readonly struct CoexistenceDecision
{
    /// <summary>Initializes a new instance of the <see cref="CoexistenceDecision"/> struct.</summary>
    /// <param name="actions">What to do.</param>
    /// <param name="displacedAttempt">The Pillion attempt whose shown proposal a competitor's replaced, or 0.</param>
    public CoexistenceDecision(CoexistenceActions actions, long displacedAttempt = 0)
    {
        Actions = actions;
        DisplacedAttempt = displacedAttempt;
    }

    /// <summary>Gets the decision to do nothing.</summary>
    public static CoexistenceDecision None => default;

    /// <summary>Gets what to do.</summary>
    public CoexistenceActions Actions { get; }

    /// <summary>Gets the Pillion attempt whose shown proposal a competitor's replaced (record <see cref="AutocompleteOutcome.Displaced"/>), or 0.</summary>
    public long DisplacedAttempt { get; }

    /// <summary>Gets a value indicating whether an action is part of the decision.</summary>
    /// <param name="action">The action.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Has(CoexistenceActions action) => (Actions & action) == action && action != CoexistenceActions.None;
}

/// <summary>
/// The coexistence state of one text view (work item 1.4): who holds the ghost-text slot, the ordering setting, and
/// the countermeasure against the Gate 0 finding that a competitor's queued proposal takes over about 8 to 30 ms after
/// Pillion's stops matching, after which the broker asks no source (not Pillion's manager, not its source) while that
/// proposal still matches what is typed. Fed with the suggestion service's events and the view's text changes; returns
/// what to do. Pure: no Visual Studio types, no threads; the caller serialises calls (the UI thread).
/// </summary>
/// <remarks>
/// The levers it relies on, measured on 18.10 (Copilot) and 17.14 (IntelliCode) and recorded in the spike's
/// FINDINGS.md: a source can answer an explicit invocation (<c>Edit.NextSuggestion</c> with nothing displayed); only a
/// dismissal with the user-Escape reason clears the broker's queued proposals (any other reason, or none, lets an
/// explicit invocation re-show the queued one without asking any source); <c>TryUpdateProposal</c> cannot replace another
/// source's displayed proposal; and the broker waits about 0.8 s for a source ordered ahead before it shows a later
/// one, after which a slower answer is queued, not shown.
/// </remarks>
public sealed class CoexistenceTimeline
{
    /// <summary>How soon after Pillion's proposal was rejected a competitor's display counts as displacing it (observed: 8 to 41 ms).</summary>
    public static readonly TimeSpan DisplacementWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a "Pillion: Suggest" request stays armed for its caret.</summary>
    public static readonly TimeSpan ArmedFor = TimeSpan.FromSeconds(10);

    /// <summary>How many times Pillion takes the slot back at one caret position (a guard against fighting the competitor).</summary>
    public const int MaxPushesPerPosition = 1;

    private readonly Func<DateTimeOffset> _now;
    private long _pillionVisible;
    private long _lastRejected;
    private DateTimeOffset _lastRejectedAt;
    private long _lastDisplaced;
    private CaretKey? _pushedAt;
    private int _pushes;
    private CaretKey? _declinedAt;
    private CaretKey? _armedAt;
    private DateTimeOffset _armedUntil;
    private bool _userCycled;

    /// <summary>Initializes a new instance of the <see cref="CoexistenceTimeline"/> class.</summary>
    /// <param name="now">The clock.</param>
    public CoexistenceTimeline(Func<DateTimeOffset> now)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>Gets or sets the ordering setting.</summary>
    public AutocompleteOrdering Ordering { get; set; }

    /// <summary>Gets or sets the countermeasure used under <see cref="AutocompleteOrdering.PillionFirst"/>.</summary>
    public CountermeasureMode Mode { get; set; } = CountermeasureMode.Replace;

    /// <summary>Gets a value indicating whether a competitor's proposal is displayed.</summary>
    public bool CompetitorDisplayed { get; private set; }

    /// <summary>Gets the attempt of the Pillion proposal displayed, or 0.</summary>
    public long PillionDisplayed => _pillionVisible;

    /// <summary>Gets the competitors whose proposals have been displayed in this view.</summary>
    public CompetitorKind Observed { get; private set; }

    /// <summary>Gets the caret a "Pillion: Suggest" request is armed for, while it is.</summary>
    public CaretKey? ArmedAt => _armedAt is { } armed && _now() <= _armedUntil ? armed : null;

    /// <summary>A Pillion proposal was displayed.</summary>
    /// <param name="attempt">Its attempt.</param>
    public void PillionShown(long attempt)
    {
        _pillionVisible = attempt;
        CompetitorDisplayed = false;
    }

    /// <summary>A displayed Pillion proposal diverged from the text or was replaced.</summary>
    /// <param name="attempt">Its attempt.</param>
    /// <param name="byUserCycling">Whether the user cycled to another proposal (<c>DivergedDueToChangeProposal</c>): not a displacement, and Pillion does not take the slot back until the text changes.</param>
    public void PillionRejected(long attempt, bool byUserCycling)
    {
        if (_pillionVisible == attempt)
        {
            _pillionVisible = 0;
        }

        if (byUserCycling)
        {
            _userCycled = true;
            _lastRejected = 0;
            return;
        }

        _lastRejected = attempt;
        _lastRejectedAt = _now();
    }

    /// <summary>A displayed Pillion proposal was accepted or dismissed: no displacement can follow.</summary>
    /// <param name="attempt">Its attempt.</param>
    public void PillionGone(long attempt)
    {
        if (_pillionVisible == attempt)
        {
            _pillionVisible = 0;
        }

        if (_lastRejected == attempt)
        {
            _lastRejected = 0;
        }
    }

    /// <summary>A competitor's proposal was displayed.</summary>
    /// <param name="kind">Whose (from its id).</param>
    /// <param name="caret">The view's caret now.</param>
    /// <returns>The decision.</returns>
    public CoexistenceDecision CompetitorShown(CompetitorKind kind, CaretKey caret)
    {
        Observed |= kind == CompetitorKind.None ? CompetitorKind.Other : kind;
        CompetitorDisplayed = true;
        long displaced = 0;
        if (_pillionVisible != 0)
        {
            displaced = _pillionVisible;
        }
        else if (_lastRejected != 0 && _now() - _lastRejectedAt <= DisplacementWindow)
        {
            displaced = _lastRejected;
        }

        _pillionVisible = 0;
        _lastRejected = 0;
        if (displaced == _lastDisplaced)
        {
            displaced = 0;
        }
        else if (displaced != 0)
        {
            _lastDisplaced = displaced;
        }

        if (!CountermeasureOn || _userCycled)
        {
            return new CoexistenceDecision(CoexistenceActions.None, displaced);
        }

        if (Mode == CountermeasureMode.Decline && PushesLeft(caret))
        {
            _declinedAt = caret;
            return new CoexistenceDecision(CoexistenceActions.RequestPillion | CoexistenceActions.DismissCompetitor, displaced);
        }

        return new CoexistenceDecision(PushesLeft(caret) ? CoexistenceActions.RequestPillion : CoexistenceActions.None, displaced);
    }

    /// <summary>The displayed competitor proposal was accepted, dismissed or diverged.</summary>
    public void CompetitorGone() => CompetitorDisplayed = false;

    /// <summary>The text changed (typing, a paste, an accept).</summary>
    /// <param name="caret">The caret after the change.</param>
    /// <returns>The decision: while a competitor's proposal is displayed the broker asks no source, so Pillion asks itself.</returns>
    public CoexistenceDecision TextChanged(CaretKey caret)
    {
        _userCycled = false;
        _declinedAt = null;
        return CompetitorDisplayed && CountermeasureOn && PushesLeft(caret)
            ? new CoexistenceDecision(CoexistenceActions.RequestPillion)
            : CoexistenceDecision.None;
    }

    /// <summary>"Pillion: Suggest": arms the caret, so the suggestion is shown when it is ready whatever the ordering and whatever is displayed.</summary>
    /// <param name="caret">The caret.</param>
    /// <returns>The decision (ask Pillion now).</returns>
    public CoexistenceDecision SuggestRequested(CaretKey caret)
    {
        _armedAt = caret;
        _armedUntil = _now() + ArmedFor;
        return new CoexistenceDecision(CoexistenceActions.RequestPillion);
    }

    /// <summary>A Pillion suggestion requested outside the broker (or arriving after the broker showed a competitor's) is ready.</summary>
    /// <param name="requestedAt">The caret it was requested for.</param>
    /// <param name="current">The view's caret now.</param>
    /// <param name="hasProposal">Whether there is a suggestion (the model may have answered with nothing).</param>
    /// <returns>The decision.</returns>
    public CoexistenceDecision PillionReady(CaretKey requestedAt, CaretKey current, bool hasProposal)
    {
        if (requestedAt != current)
        {
            return CoexistenceDecision.None;
        }

        if (ArmedAt == current)
        {
            _armedAt = null;
            return hasProposal ? Push(current) : CoexistenceDecision.None;
        }

        switch (Ordering)
        {
            case AutocompleteOrdering.PillionFirst when CountermeasureOn:
                if (_declinedAt == current)
                {
                    _declinedAt = null;
                    return hasProposal ? Push(current) : new CoexistenceDecision(CoexistenceActions.RequestAll);
                }

                return hasProposal && CompetitorDisplayed && !_userCycled && PushesLeft(current) ? Push(current) : CoexistenceDecision.None;

            case AutocompleteOrdering.CompetitorFirst:
                // Fill in only when nothing at all is displayed once the competitor had its chance.
                return hasProposal && !CompetitorDisplayed && _pillionVisible == 0 && PushesLeft(current) ? Push(current) : CoexistenceDecision.None;

            default:
                return CoexistenceDecision.None;
        }
    }

    private bool CountermeasureOn => Ordering == AutocompleteOrdering.PillionFirst && Mode != CountermeasureMode.Off;

    private bool PushesLeft(CaretKey caret) => _pushedAt != caret || _pushes < MaxPushesPerPosition;

    private CoexistenceDecision Push(CaretKey caret)
    {
        if (_pushedAt != caret)
        {
            _pushedAt = caret;
            _pushes = 0;
        }

        _pushes++;
        return new CoexistenceDecision(CoexistenceActions.PushPillion);
    }
}

/// <summary>What Pillion's proposal source does with a request from the editor.</summary>
public enum SourceAnswer
{
    /// <summary>Answer with nothing (and cancel any attempt in flight).</summary>
    Nothing,

    /// <summary>Run the pipeline and answer with its suggestion.</summary>
    Request,

    /// <summary>Answer with nothing now so the competitor can be shown, and run the pipeline in the background to fill in.</summary>
    RequestInBackground,

    /// <summary>Answer at once with the suggestion kept for exactly this caret by a push.</summary>
    FromKept,
}

/// <summary>The ordering rules that do not depend on the timeline: what the proposal manager and source answer.</summary>
public static class CoexistencePolicy
{
    /// <summary>
    /// How long after a request <see cref="AutocompleteOrdering.CompetitorFirst"/> waits before filling in: the broker
    /// waits about 0.8 s for a source ordered ahead, and Copilot's answers took 0.25 to 0.8 s in the measurements.
    /// </summary>
    public static readonly TimeSpan CompetitorGrace = TimeSpan.FromSeconds(1);

    /// <summary>Decides the proposal manager's answer (it runs first in the chain).</summary>
    /// <param name="ordering">The ordering setting.</param>
    /// <param name="competitorPresent">Whether a competitor's source is present or one of its proposals was seen.</param>
    /// <param name="keptForCaret">Whether a push kept a Pillion suggestion for exactly this caret.</param>
    /// <returns><see langword="true"/> to say "propose here"; <see langword="false"/> to defer to the next manager (Copilot's or IntelliCode's, whose own rules then apply).</returns>
    public static bool ManagerProposes(AutocompleteOrdering ordering, bool competitorPresent, bool keptForCaret) =>
        keptForCaret || ordering switch
        {
            AutocompleteOrdering.PillionFirst => true,
            AutocompleteOrdering.CompetitorFirst => !competitorPresent,
            _ => false,
        };

    /// <summary>Decides how Pillion's source answers a request.</summary>
    /// <param name="ordering">The ordering setting.</param>
    /// <param name="competitorPresent">Whether a competitor's source is present or one of its proposals was seen.</param>
    /// <param name="keptForCaret">Whether a push kept a Pillion suggestion for exactly this caret.</param>
    /// <returns>The answer.</returns>
    public static SourceAnswer Source(AutocompleteOrdering ordering, bool competitorPresent, bool keptForCaret)
    {
        if (keptForCaret)
        {
            return SourceAnswer.FromKept;
        }

        return ordering switch
        {
            AutocompleteOrdering.PillionFirst => SourceAnswer.Request,
            AutocompleteOrdering.CompetitorFirst => competitorPresent ? SourceAnswer.RequestInBackground : SourceAnswer.Request,
            _ => SourceAnswer.Nothing,
        };
    }

    /// <summary>Reads the countermeasure override used for measurements (<c>PILLION_AUTOCOMPLETE_COUNTERMEASURE</c>).</summary>
    /// <param name="value">The variable's value: <c>off</c>, <c>replace</c> or <c>decline</c>.</param>
    /// <returns>The mode; anything else is <see cref="CountermeasureMode.Replace"/>.</returns>
    public static CountermeasureMode ParseMode(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "OFF" => CountermeasureMode.Off,
        "DECLINE" => CountermeasureMode.Decline,
        _ => CountermeasureMode.Replace,
    };
}
