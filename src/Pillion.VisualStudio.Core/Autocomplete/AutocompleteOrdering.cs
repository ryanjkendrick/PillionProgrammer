using System;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Who gets the ghost-text slot when another inline-suggestion provider (GitHub Copilot, IntelliCode whole-line
/// completions) is also on: the <c>autocomplete.ordering</c> setting (design section 5, Suggestion coexistence).
/// </summary>
public enum AutocompleteOrdering
{
    /// <summary>
    /// <c>pillionFirst</c>, the default: Pillion asks for a suggestion on every keystroke and takes the slot back when
    /// a competitor's queued proposal replaced one of Pillion's (see <see cref="CoexistenceTimeline"/>).
    /// </summary>
    PillionFirst,

    /// <summary>
    /// <c>competitorFirst</c>: while a competitor is present Pillion's proposal manager defers, Pillion's source answers
    /// the editor with nothing, and a Pillion suggestion is shown only when no other proposal is displayed once it is ready.
    /// </summary>
    CompetitorFirst,

    /// <summary><c>onDemand</c>: no automatic requests; "Pillion: Suggest" asks for one suggestion at the caret.</summary>
    OnDemand,
}

/// <summary>Reads and writes <see cref="AutocompleteOrdering"/> as the <c>autocomplete.ordering</c> setting value.</summary>
public static class AutocompleteOrderingText
{
    /// <summary>The setting value of <see cref="AutocompleteOrdering.PillionFirst"/>.</summary>
    public const string PillionFirst = "pillionFirst";

    /// <summary>The setting value of <see cref="AutocompleteOrdering.CompetitorFirst"/>.</summary>
    public const string CompetitorFirst = "competitorFirst";

    /// <summary>The setting value of <see cref="AutocompleteOrdering.OnDemand"/>.</summary>
    public const string OnDemand = "onDemand";

    /// <summary>Reads a setting value.</summary>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    /// <returns>The ordering; a missing or unknown value is <see cref="AutocompleteOrdering.PillionFirst"/> (case-insensitive).</returns>
    public static AutocompleteOrdering Parse(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, CompetitorFirst, StringComparison.OrdinalIgnoreCase))
        {
            return AutocompleteOrdering.CompetitorFirst;
        }

        return string.Equals(text, OnDemand, StringComparison.OrdinalIgnoreCase)
            ? AutocompleteOrdering.OnDemand
            : AutocompleteOrdering.PillionFirst;
    }

    /// <summary>Gets the setting value of an ordering.</summary>
    /// <param name="ordering">The ordering.</param>
    /// <returns><c>pillionFirst</c>, <c>competitorFirst</c> or <c>onDemand</c>.</returns>
    public static string ToSettingValue(AutocompleteOrdering ordering) => ordering switch
    {
        AutocompleteOrdering.CompetitorFirst => CompetitorFirst,
        AutocompleteOrdering.OnDemand => OnDemand,
        _ => PillionFirst,
    };
}
