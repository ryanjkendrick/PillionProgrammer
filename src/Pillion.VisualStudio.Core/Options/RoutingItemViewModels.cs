using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One entry of a dropdown on the Models and routing page: the value written to settings and the text shown.</summary>
public sealed class OptionChoice
{
    /// <summary>Initializes a new instance of the <see cref="OptionChoice"/> class.</summary>
    /// <param name="value">The value as it appears in <c>settings.json</c> (for example <c>localFast</c>).</param>
    /// <param name="label">The text shown to the user (for example <c>Local-fast</c>).</param>
    public OptionChoice(string value, string label)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    /// <summary>Gets the value as it appears in <c>settings.json</c>.</summary>
    public string Value { get; }

    /// <summary>Gets the text shown to the user.</summary>
    public string Label { get; }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>The profiles, task classes and GPU tiers the Models and routing page offers, with their settings names.</summary>
public static class RoutingChoices
{
    /// <summary>The profiles a task class can be assigned to, <see cref="Profile.Auto"/> first.</summary>
    public static IReadOnlyList<OptionChoice> Profiles { get; } =
    [
        new("auto", "Auto"),
        new("fast", "Fast"),
        new("balanced", "Balanced"),
        new("frontier", "Frontier"),
        new("localFast", "Local-fast"),
        new("localStrong", "Local-strong"),
    ];

    /// <summary>
    /// The GPU tier override choices, matching what <c>models.gpuTier</c> accepts (<c>GpuTierSetting</c> in the host):
    /// <c>auto</c> detects the tier; the others override detection.
    /// </summary>
    public static IReadOnlyList<OptionChoice> GpuTiers { get; } =
    [
        new("auto", "Auto (detect)"),
        new("cpu", "CPU only"),
        new("8gb", "8 GB GPU"),
        new("12gb", "12 GB GPU"),
        new("16gb", "16 GB GPU"),
        new("24gb", "24 GB or more"),
    ];

    /// <summary>Gets the settings name of a profile (the property under <c>routing.profiles</c> and the value under <c>routing.tasks</c>).</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>For example <c>localFast</c>.</returns>
    public static string NameOf(Profile profile) => profile switch
    {
        Profile.Auto => "auto",
        Profile.Fast => "fast",
        Profile.Balanced => "balanced",
        Profile.Frontier => "frontier",
        Profile.LocalFast => "localFast",
        Profile.LocalStrong => "localStrong",
        _ => char.ToLowerInvariant(profile.ToString()[0]) + profile.ToString().Substring(1),
    };

    /// <summary>Gets the settings name of a task class (the property under <c>routing.tasks</c> and <c>cloudSwitches</c>).</summary>
    /// <param name="taskClass">The task class.</param>
    /// <returns>For example <c>inlineEdit</c>.</returns>
    public static string NameOf(TaskClass taskClass)
    {
        var name = taskClass.ToString();
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>Finds the choice with the given value (ignoring case).</summary>
    /// <param name="choices">The choices.</param>
    /// <param name="value">The value read from settings, or <see langword="null"/>.</param>
    /// <returns>The choice, or <see langword="null"/> when the value is not one of them.</returns>
    public static OptionChoice? Find(IReadOnlyList<OptionChoice> choices, string? value)
    {
        if (choices is null || value is null)
        {
            return null;
        }

        foreach (var choice in choices)
        {
            if (string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        return null;
    }
}

/// <summary>One row of the task class grid: the profile the class uses and whether it may use cloud providers.</summary>
public sealed class TaskRouteViewModel : ObservableObject
{
    private OptionChoice _selectedProfile = RoutingChoices.Profiles[0];
    private bool _cloudSwitchOn;
    private bool _cloudSwitchEnabled = true;

    /// <summary>Initializes a new instance of the <see cref="TaskRouteViewModel"/> class.</summary>
    /// <param name="taskClass">The task class.</param>
    /// <param name="label">The label.</param>
    /// <param name="description">The one-line explanation.</param>
    /// <param name="cloudSwitchKey">The property under <c>cloudSwitches</c>, or <see langword="null"/> when the class has no switch (it always runs locally).</param>
    /// <param name="cloudSwitchDefault">The switch value when the setting is missing (the host's default).</param>
    /// <param name="cloudSwitchNote">The one-line explanation of the switch, or an empty string.</param>
    /// <param name="noSwitchText">The short text shown instead of a switch when the class has none.</param>
    public TaskRouteViewModel(TaskClass taskClass, string label, string description, string? cloudSwitchKey, bool cloudSwitchDefault, string cloudSwitchNote, string noSwitchText = "Local only")
    {
        NoSwitchText = noSwitchText;
        TaskClass = taskClass;
        Key = RoutingChoices.NameOf(taskClass);
        Label = label;
        Description = description;
        CloudSwitchKey = cloudSwitchKey;
        CloudSwitchDefault = cloudSwitchDefault;
        CloudSwitchNote = cloudSwitchNote;
        _cloudSwitchOn = cloudSwitchDefault;
    }

    /// <summary>Raised when the profile or the cloud switch is changed by the user.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the task class.</summary>
    public TaskClass TaskClass { get; }

    /// <summary>Gets the property name under <c>routing.tasks</c>.</summary>
    public string Key { get; }

    /// <summary>Gets the label.</summary>
    public string Label { get; }

    /// <summary>Gets the one-line explanation of the task class.</summary>
    public string Description { get; }

    /// <summary>Gets the profiles to choose from (an instance member so the view can bind to it).</summary>
#pragma warning disable CA1822 // Bound from XAML.
    public IReadOnlyList<OptionChoice> ProfileChoices => RoutingChoices.Profiles;
#pragma warning restore CA1822

    /// <summary>Gets the property name under <c>cloudSwitches</c>, or <see langword="null"/> when the class has no switch.</summary>
    public string? CloudSwitchKey { get; }

    /// <summary>Gets a value indicating whether the class has a cloud switch (Embedding has none on this page: it is local unless <c>cloudSwitches.embedding</c> is set in settings.json).</summary>
    public bool HasCloudSwitch => CloudSwitchKey is not null;

    /// <summary>Gets the short text shown instead of a switch when the class has none.</summary>
    public string NoSwitchText { get; }

    /// <summary>Gets the switch value used when the setting is missing.</summary>
    public bool CloudSwitchDefault { get; }

    /// <summary>Gets the one-line explanation of the cloud switch.</summary>
    public string CloudSwitchNote { get; }

    /// <summary>Gets or sets the profile the class uses.</summary>
    public OptionChoice SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is not null && SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(Warning));
                OnPropertyChanged(nameof(HasWarning));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether the class may use cloud providers. Ignored when <see cref="HasCloudSwitch"/> is <see langword="false"/>.</summary>
    public bool CloudSwitchOn
    {
        get => _cloudSwitchOn;
        set
        {
            if (SetProperty(ref _cloudSwitchOn, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets a value indicating whether the cloud switch can be changed (not while Local-only mode is on).</summary>
    public bool CloudSwitchEnabled
    {
        get => _cloudSwitchEnabled;
        internal set => SetProperty(ref _cloudSwitchEnabled, value);
    }

    /// <summary>
    /// Gets a note about the chosen profile, or an empty string: autocomplete needs a fill-in-the-middle model, which
    /// only the Fast and Local-fast profiles (and Auto) are expected to give.
    /// </summary>
    public string Warning =>
        TaskClass == TaskClass.Autocomplete && SelectedProfile.Value is "balanced" or "frontier" or "localStrong"
            ? "Autocomplete needs a fill-in-the-middle model. " + SelectedProfile.Label + " may map to a chat-only model; Fast, Local-fast or Auto are the usual choices."
            : string.Empty;

    /// <summary>Gets a value indicating whether <see cref="Warning"/> is not empty.</summary>
    public bool HasWarning => Warning.Length > 0;

    /// <summary>Gets the value under <c>routing.tasks</c> as it was read from settings (normalised to a known profile).</summary>
    internal string LoadedProfile { get; private set; } = "auto";

    /// <summary>Gets the switch value as it was read from settings.</summary>
    internal bool LoadedCloudSwitch { get; private set; }

    /// <summary>Sets the values read from settings without raising <see cref="Changed"/>.</summary>
    /// <param name="profileValue">The raw value under <c>routing.tasks</c>, or <see langword="null"/>.</param>
    /// <param name="cloudSwitch">The switch value.</param>
    internal void Load(string? profileValue, bool cloudSwitch)
    {
        var choice = RoutingChoices.Find(RoutingChoices.Profiles, profileValue) ?? RoutingChoices.Profiles[0];
        _selectedProfile = choice;
        LoadedProfile = choice.Value;
        _cloudSwitchOn = cloudSwitch;
        LoadedCloudSwitch = cloudSwitch;
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(CloudSwitchOn));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
    }

    /// <summary>Records the saved values as the loaded ones.</summary>
    internal void MarkSaved()
    {
        LoadedProfile = SelectedProfile.Value;
        LoadedCloudSwitch = CloudSwitchOn;
    }
}

/// <summary>One row of the profile grid: an optional model id that replaces the model catalog's choice for the profile.</summary>
public sealed class ProfileOverrideViewModel : ObservableObject
{
    private string _modelId = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="ProfileOverrideViewModel"/> class.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="label">The label.</param>
    /// <param name="isLocal">Whether the profile runs on this machine (decides which listed models are suggested).</param>
    public ProfileOverrideViewModel(Profile profile, string label, bool isLocal)
    {
        Profile = profile;
        Key = RoutingChoices.NameOf(profile);
        Label = label;
        IsLocal = isLocal;
    }

    /// <summary>Raised when the model id is changed by the user.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the profile.</summary>
    public Profile Profile { get; }

    /// <summary>Gets the property name under <c>routing.profiles</c>.</summary>
    public string Key { get; }

    /// <summary>Gets the label.</summary>
    public string Label { get; }

    /// <summary>Gets a value indicating whether the profile runs on this machine.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets the model ids listed by the host's providers that suit the profile, offered as suggestions.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>
    /// Gets or sets the model id that replaces the catalog default (<c>modelId</c> or <c>providerId/modelId</c>), or an
    /// empty string to use the default.
    /// </summary>
    public string ModelId
    {
        get => _modelId;
        set
        {
            if (SetProperty(ref _modelId, value ?? string.Empty))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets the hint shown under the field.</summary>
    public string Hint =>
        Suggestions.Count > 0
            ? "Empty uses the catalog default. Pick a listed model or type an id, optionally as provider/model."
            : "Empty uses the catalog default. The host lists no " + (IsLocal ? "local" : "cloud") + " models right now; type an id, optionally as provider/model.";

    /// <summary>Gets the value as it was read from settings.</summary>
    internal string LoadedModelId { get; private set; } = string.Empty;

    /// <summary>Sets the value read from settings without raising <see cref="Changed"/>.</summary>
    /// <param name="modelId">The raw value, or <see langword="null"/>.</param>
    internal void Load(string? modelId)
    {
        _modelId = modelId ?? string.Empty;
        LoadedModelId = _modelId;
        OnPropertyChanged(nameof(ModelId));
    }

    /// <summary>Replaces the suggestions.</summary>
    /// <param name="ids">The model ids to suggest.</param>
    internal void SetSuggestions(IEnumerable<string> ids)
    {
        Suggestions.Clear();
        foreach (var id in ids)
        {
            Suggestions.Add(id);
        }

        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Records the saved value as the loaded one.</summary>
    internal void MarkSaved() => LoadedModelId = ModelId.Trim();

    /// <summary>Checks the model id.</summary>
    /// <returns>A message describing the problem, or <see langword="null"/> when the value can be saved.</returns>
    internal string? Validate()
    {
        var id = ModelId.Trim();
        if (id.Length > 200)
        {
            return Label + ": the model id is longer than 200 characters.";
        }

        foreach (var c in id)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return Label + ": a model id cannot contain spaces or control characters.";
            }
        }

        return null;
    }
}
