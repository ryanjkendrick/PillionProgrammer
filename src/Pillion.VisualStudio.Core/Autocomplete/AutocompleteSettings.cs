using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The autocomplete settings the extension reads from Pillion Host: the <c>autocomplete</c> feature flag (from
/// <c>effective.features</c>) and the <c>autocomplete</c> section of <c>settings.json</c>:
/// <code>
/// "autocomplete": {
///   "disabledLanguages": [ "python" ],
///   "announceSuggestions": true,
///   "ordering": "pillionFirst",
///   "coexistencePromptDone": false
/// }
/// </code>
/// The section is not a typed part of Pillion Host's settings model: the host keeps it as it is (unknown sections are
/// preserved) and only the extension reads it, as JSON. <c>ordering</c> is <c>pillionFirst</c> (the default),
/// <c>competitorFirst</c> or <c>onDemand</c> (<see cref="AutocompleteOrdering"/>); <c>coexistencePromptDone</c> records,
/// once per machine, that the user answered the "inline suggestions are also on" info bar.
/// Immutable; a refresh replaces the instance.
/// </summary>
public sealed class AutocompleteSettings
{
    /// <summary>The settings key of the autocomplete section.</summary>
    public const string SectionKey = "autocomplete";

    /// <summary>The name of the feature flag.</summary>
    public const string FeatureFlag = "autocomplete";

    /// <summary>The property that lists the language identifiers the user switched off.</summary>
    public const string DisabledLanguagesProperty = "disabledLanguages";

    /// <summary>The property that switches the screen-reader announcement of new suggestions.</summary>
    public const string AnnounceSuggestionsProperty = "announceSuggestions";

    /// <summary>The property that sets who gets the ghost-text slot when another provider is on (<see cref="AutocompleteOrdering"/>).</summary>
    public const string OrderingProperty = "ordering";

    /// <summary>The property that records that the coexistence info bar was answered (once per machine).</summary>
    public const string CoexistencePromptDoneProperty = "coexistencePromptDone";

    /// <summary>Gets the defaults: enabled, no language switched off, announcements on.</summary>
    public static AutocompleteSettings Default { get; } = new(enabled: true, [], announceSuggestions: true);

    /// <summary>Initializes a new instance of the <see cref="AutocompleteSettings"/> class.</summary>
    /// <param name="enabled">Whether the feature flag is on.</param>
    /// <param name="disabledLanguages">The language identifiers the user switched off.</param>
    /// <param name="announceSuggestions">Whether new suggestions raise a UI Automation notification.</param>
    /// <param name="ordering">Who gets the ghost-text slot when another provider is on.</param>
    /// <param name="coexistencePromptDone">Whether the coexistence info bar was answered.</param>
    public AutocompleteSettings(
        bool enabled,
        IReadOnlyCollection<string> disabledLanguages,
        bool announceSuggestions,
        AutocompleteOrdering ordering = AutocompleteOrdering.PillionFirst,
        bool coexistencePromptDone = false)
    {
        Enabled = enabled;
        DisabledLanguages = disabledLanguages ?? throw new ArgumentNullException(nameof(disabledLanguages));
        AnnounceSuggestions = announceSuggestions;
        Ordering = ordering;
        CoexistencePromptDone = coexistencePromptDone;
    }

    /// <summary>Gets a value indicating whether the <c>autocomplete</c> feature flag is on.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the language identifiers the user switched off (as in <see cref="DocumentLanguage"/>).</summary>
    public IReadOnlyCollection<string> DisabledLanguages { get; }

    /// <summary>Gets a value indicating whether new suggestions are announced to screen readers.</summary>
    public bool AnnounceSuggestions { get; }

    /// <summary>Gets who gets the ghost-text slot when another inline-suggestion provider is on.</summary>
    public AutocompleteOrdering Ordering { get; }

    /// <summary>Gets a value indicating whether the coexistence info bar was answered on this machine.</summary>
    public bool CoexistencePromptDone { get; }

    /// <summary>Reads the feature flag from the JSON of <c>effective.features</c>.</summary>
    /// <param name="featuresJson">The JSON object, or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> only when the object says <c>"autocomplete": false</c>; an absent key means on.</returns>
    public static bool ReadFeatureFlag(string? featuresJson) =>
        !(SettingsJsonText.ReadObject(featuresJson).TryGetValue(FeatureFlag, StringComparison.Ordinal, out var token)
            && token is JValue { Type: JTokenType.Boolean } value
            && !(bool)value);

    /// <summary>Builds the settings from the host's answers.</summary>
    /// <param name="enabled">The feature flag.</param>
    /// <param name="sectionJson">The JSON of the <c>autocomplete</c> section, or <see langword="null"/> when it is not set.</param>
    /// <returns>The settings; unreadable or missing values take their defaults.</returns>
    public static AutocompleteSettings FromJson(bool enabled, string? sectionJson)
    {
        JObject section;
        try
        {
            section = SettingsJsonText.ReadObject(sectionJson);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            section = [];
        }

        string[] languages = section.TryGetValue(DisabledLanguagesProperty, StringComparison.Ordinal, out var list) && list is JArray array
            ? array.OfType<JValue>()
                .Where(v => v.Type == JTokenType.String)
                .Select(v => ((string)v!).Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var announce = !(section.TryGetValue(AnnounceSuggestionsProperty, StringComparison.Ordinal, out var flag)
            && flag is JValue { Type: JTokenType.Boolean } announceValue
            && !(bool)announceValue);
        var ordering = section.TryGetValue(OrderingProperty, StringComparison.Ordinal, out var orderingToken)
            && orderingToken is JValue { Type: JTokenType.String } orderingValue
            ? AutocompleteOrderingText.Parse((string?)orderingValue)
            : AutocompleteOrdering.PillionFirst;
        var promptDone = section.TryGetValue(CoexistencePromptDoneProperty, StringComparison.Ordinal, out var doneToken)
            && doneToken is JValue { Type: JTokenType.Boolean } doneValue
            && (bool)doneValue;
        return new AutocompleteSettings(enabled, languages, announce, ordering, promptDone);
    }
}
