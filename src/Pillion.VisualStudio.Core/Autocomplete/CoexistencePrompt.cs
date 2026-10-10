namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>An answer to the coexistence info bar.</summary>
public enum CoexistenceChoice
{
    /// <summary>"Pillion first": <c>autocomplete.ordering</c> becomes <c>pillionFirst</c>.</summary>
    PillionFirst,

    /// <summary>"Copilot first" (or IntelliCode): <c>autocomplete.ordering</c> becomes <c>competitorFirst</c>.</summary>
    CompetitorFirst,

    /// <summary>"Pillion on demand only": <c>autocomplete.ordering</c> becomes <c>onDemand</c>.</summary>
    OnDemand,

    /// <summary>"Keep both as they are": the ordering is left as it is.</summary>
    KeepBoth,

    /// <summary>"Don't show again": nothing changes, and the bar is not shown again on this machine.</summary>
    DontShowAgain,

    /// <summary>The bar was closed with its close button: nothing changes, and it is shown again at the next start.</summary>
    Closed,
}

/// <summary>What answering the coexistence info bar writes to the <c>autocomplete</c> settings section.</summary>
public sealed class CoexistenceAnswer
{
    /// <summary>Initializes a new instance of the <see cref="CoexistenceAnswer"/> class.</summary>
    /// <param name="ordering">The new <c>autocomplete.ordering</c>, or <see langword="null"/> to leave it.</param>
    /// <param name="markDone">Whether to set <c>autocomplete.coexistencePromptDone</c>.</param>
    public CoexistenceAnswer(AutocompleteOrdering? ordering, bool markDone)
    {
        Ordering = ordering;
        MarkDone = markDone;
    }

    /// <summary>Gets the new <c>autocomplete.ordering</c>, or <see langword="null"/> to leave it.</summary>
    public AutocompleteOrdering? Ordering { get; }

    /// <summary>Gets a value indicating whether to set <c>autocomplete.coexistencePromptDone</c>, so the bar is not shown again.</summary>
    public bool MarkDone { get; }
}

/// <summary>
/// The once-per-machine coexistence info bar (design section 5): "Copilot inline suggestions are also on. Choose:
/// Pillion first, Copilot first, Pillion on demand only, or keep both as they are", with "Don't show again" and a link
/// to the other product's own setting. Pillion never changes the other product's settings itself.
/// </summary>
public static class CoexistencePrompt
{
    /// <summary>Decides whether to show the bar now, and naming whom.</summary>
    /// <param name="settings">The current autocomplete settings (the once-per-machine flag).</param>
    /// <param name="detection">The start-up detection.</param>
    /// <param name="observed">The competitors whose proposals have been displayed in this session.</param>
    /// <param name="shownThisSession">Whether the bar was already shown in this Visual Studio session.</param>
    /// <returns>The competitors to name, or <see cref="CompetitorKind.None"/> to show nothing.</returns>
    public static CompetitorKind ToPrompt(AutocompleteSettings settings, CompetitorDetection detection, CompetitorKind observed, bool shownThisSession)
    {
        if (settings is null)
        {
            throw new System.ArgumentNullException(nameof(settings));
        }

        if (detection is null)
        {
            throw new System.ArgumentNullException(nameof(detection));
        }

        if (!settings.Enabled || settings.CoexistencePromptDone || shownThisSession)
        {
            return CompetitorKind.None;
        }

        // A product whose own setting says off does not count even if one of its proposals was seen earlier.
        var notOff = ~((detection.CopilotCompletions == CompetitorSetting.Off ? CompetitorKind.Copilot : CompetitorKind.None)
            | (detection.IntelliCodeCompletions == CompetitorSetting.Off ? CompetitorKind.IntelliCode : CompetitorKind.None));
        return (detection.ActiveAtStartup | observed) & notOff;
    }

    /// <summary>Gets the bar's message.</summary>
    /// <param name="competitors">The competitors to name.</param>
    /// <returns>For example "Copilot inline suggestions are also on. Choose:".</returns>
    public static string Message(CompetitorKind competitors) =>
        CompetitorProposals.DisplayName(competitors) + " inline suggestions are also on. Choose:";

    /// <summary>Gets the label of a choice.</summary>
    /// <param name="choice">The choice.</param>
    /// <param name="competitors">The competitors named in the bar.</param>
    /// <returns>The label.</returns>
    public static string Label(CoexistenceChoice choice, CompetitorKind competitors) => choice switch
    {
        CoexistenceChoice.PillionFirst => "Pillion first",
        CoexistenceChoice.CompetitorFirst => CompetitorProposals.DisplayName(competitors) + " first",
        CoexistenceChoice.OnDemand => "Pillion on demand only",
        CoexistenceChoice.KeepBoth => "Keep both as they are",
        CoexistenceChoice.DontShowAgain => "Don't show again",
        _ => "Close",
    };

    /// <summary>Gets what a choice writes.</summary>
    /// <param name="choice">The choice.</param>
    /// <returns>The answer.</returns>
    public static CoexistenceAnswer Apply(CoexistenceChoice choice) => choice switch
    {
        CoexistenceChoice.PillionFirst => new(AutocompleteOrdering.PillionFirst, markDone: true),
        CoexistenceChoice.CompetitorFirst => new(AutocompleteOrdering.CompetitorFirst, markDone: true),
        CoexistenceChoice.OnDemand => new(AutocompleteOrdering.OnDemand, markDone: true),
        CoexistenceChoice.KeepBoth or CoexistenceChoice.DontShowAgain => new(null, markDone: true),
        _ => new(null, markDone: false),
    };
}
