using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Core;

/// <summary>One choice of the ordering setting on the Autocomplete page.</summary>
public sealed class OrderingChoice
{
    /// <summary>Initializes a new instance of the <see cref="OrderingChoice"/> class.</summary>
    /// <param name="ordering">The ordering.</param>
    /// <param name="label">The label.</param>
    /// <param name="description">The one-line explanation.</param>
    public OrderingChoice(AutocompleteOrdering ordering, string label, string description)
    {
        Ordering = ordering;
        Label = label;
        Description = description;
    }

    /// <summary>Gets the ordering.</summary>
    public AutocompleteOrdering Ordering { get; }

    /// <summary>Gets the label.</summary>
    public string Label { get; }

    /// <summary>Gets the one-line explanation.</summary>
    public string Description { get; }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// The Tools &gt; Options &gt; Pillion Programmer &gt; Autocomplete page: the ordering against other inline-suggestion
/// providers (<c>autocomplete.ordering</c>), the languages switched off (<c>autocomplete.disabledLanguages</c>), the
/// screen-reader announcement (<c>autocomplete.announceSuggestions</c>) and whether the coexistence info bar may ask
/// again (<c>autocomplete.coexistencePromptDone</c>). Saves the whole <c>autocomplete</c> section, keeping properties this
/// version does not show.
/// </summary>
public sealed class AutocompleteOptionsViewModel : OptionsPageViewModel
{
    private OrderingChoice _ordering;
    private string _disabledLanguages = string.Empty;
    private bool _announceSuggestions = true;
    private bool _askAgain;
    private JObject _loaded = [];

    /// <summary>Initializes a new instance of the <see cref="AutocompleteOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public AutocompleteOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log = null)
        : base(connection, joinableTaskFactory, log)
    {
        Orderings =
        [
            new(AutocompleteOrdering.PillionFirst, "Pillion first", "Pillion's suggestion takes the slot whenever it has one; another provider's proposal shows when Pillion has none."),
            new(AutocompleteOrdering.CompetitorFirst, "Other provider first", "Copilot's or IntelliCode's proposal shows first; Pillion fills in only when nothing else is shown."),
            new(AutocompleteOrdering.OnDemand, "Pillion on demand only", "No automatic Pillion suggestions; use Pillion: Suggest (Ctrl+Alt+Shift+P, then S) to ask for one."),
        ];
        _ordering = Orderings[0];
    }

    /// <summary>Gets the ordering choices.</summary>
    public IReadOnlyList<OrderingChoice> Orderings { get; }

    /// <summary>Gets or sets the chosen ordering.</summary>
    public OrderingChoice Ordering
    {
        get => _ordering;
        set
        {
            if (value is not null && SetProperty(ref _ordering, value))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets the language identifiers switched off, separated by commas (for example <c>python, xaml</c>).</summary>
    public string DisabledLanguages
    {
        get => _disabledLanguages;
        set
        {
            if (SetProperty(ref _disabledLanguages, value ?? string.Empty))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether new suggestions are announced to screen readers.</summary>
    public bool AnnounceSuggestions
    {
        get => _announceSuggestions;
        set
        {
            if (SetProperty(ref _announceSuggestions, value))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether the "inline suggestions are also on" info bar may be shown again.</summary>
    public bool AskAgain
    {
        get => _askAgain;
        set
        {
            if (SetProperty(ref _askAgain, value))
            {
                IsDirty = true;
            }
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> WatchedSections { get; } = [AutocompleteSettings.SectionKey];

    /// <summary>Splits the disabled-languages text into identifiers.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The trimmed, distinct, non-empty identifiers.</returns>
    public static IReadOnlyList<string> SplitLanguages(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <inheritdoc/>
    protected override async Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        var section = SettingsJsonText.ReadObject(await settings.GetAsync(AutocompleteSettings.SectionKey, cancellationToken).ConfigureAwait(false));
        return page => ((AutocompleteOptionsViewModel)page).ApplyLoaded(section);
    }

    /// <inheritdoc/>
    protected override Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave()
    {
        var section = (JObject)_loaded.DeepClone();
        section[AutocompleteSettings.OrderingProperty] = AutocompleteOrderingText.ToSettingValue(Ordering.Ordering);
        section[AutocompleteSettings.DisabledLanguagesProperty] = new JArray(SplitLanguages(DisabledLanguages).Cast<object>().ToArray());
        section[AutocompleteSettings.AnnounceSuggestionsProperty] = AnnounceSuggestions;
        section[AutocompleteSettings.CoexistencePromptDoneProperty] = !AskAgain;
        var json = SettingsJsonText.Write(section);
        return async (settings, cancellationToken) =>
        {
            await settings.SetAsync(AutocompleteSettings.SectionKey, json, cancellationToken).ConfigureAwait(false);
            return page => ((AutocompleteOptionsViewModel)page)._loaded = section;
        };
    }

    private void ApplyLoaded(JObject section)
    {
        _loaded = section;
        var settings = AutocompleteSettings.FromJson(enabled: true, section.ToString(Newtonsoft.Json.Formatting.None));
        _ordering = Orderings.First(o => o.Ordering == settings.Ordering);
        _disabledLanguages = string.Join(", ", settings.DisabledLanguages);
        _announceSuggestions = settings.AnnounceSuggestions;
        _askAgain = !settings.CoexistencePromptDone;
        OnPropertyChanged(nameof(Ordering));
        OnPropertyChanged(nameof(DisabledLanguages));
        OnPropertyChanged(nameof(AnnounceSuggestions));
        OnPropertyChanged(nameof(AskAgain));
    }
}
