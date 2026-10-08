using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One provider on the Providers and keys page. Edits a copy of the provider's settings object, so properties this
/// version does not know about survive a save. The API key is held only until the page is saved, when it goes to the
/// Credential Manager; it is never written to settings, logged or read back.
/// </summary>
public sealed class ProviderItemViewModel : ObservableObject
{
    /// <summary>The provider kinds the settings file accepts, as written in it.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["ollama", "openai-compatible", "anthropic", "openai", "azure-openai", "gemini", "mistral"];

    private readonly JObject _original;
    private string _id;
    private string _kind;
    private string _displayName;
    private string _endpoint;
    private string _modelsText;
    private bool _enabled;
    private string _apiKey = string.Empty;
    private bool _hasStoredKey;
    private string _testResult = string.Empty;

    private ProviderItemViewModel(JObject original, bool isNew)
    {
        _original = original;
        IsNew = isNew;
        _id = SettingsJsonText.GetString(original, "id");
        var kind = SettingsJsonText.GetString(original, "kind");
        _kind = kind.Length == 0 ? "openai-compatible" : kind;
        _displayName = SettingsJsonText.GetString(original, "displayName");
        _endpoint = SettingsJsonText.GetString(original, "endpoint");
        _modelsText = string.Join(Environment.NewLine, ReadModels(original));
        _enabled = original["enabled"] is JValue { Type: JTokenType.Boolean } enabled ? (bool)enabled : true;
    }

    /// <summary>Raised when a field changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether the provider has not been saved yet (only then can its id change).</summary>
    public bool IsNew { get; private set; }

    /// <summary>Gets the id the provider was loaded with (its key's name), or an empty string for a new provider.</summary>
    public string SavedId { get; private set; } = string.Empty;

    /// <summary>Gets or sets the provider id, which is also the name of its key in the Credential Manager.</summary>
    public string Id
    {
        get => _id;
        set
        {
            if (IsNew && Set(ref _id, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(KeyStatusText));
            }
        }
    }

    /// <summary>Gets or sets the provider kind (one of <see cref="Kinds"/>).</summary>
    public string Kind
    {
        get => _kind;
        set => Set(ref _kind, value ?? string.Empty);
    }

    /// <summary>Gets or sets the display name.</summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>Gets or sets the endpoint URL; empty for the provider's default.</summary>
    public string Endpoint
    {
        get => _endpoint;
        set => Set(ref _endpoint, (value ?? string.Empty).Trim());
    }

    /// <summary>Gets or sets the model ids, one per line.</summary>
    public string ModelsText
    {
        get => _modelsText;
        set => Set(ref _modelsText, value ?? string.Empty);
    }

    /// <summary>Gets the model ids parsed from <see cref="ModelsText"/>.</summary>
    public IReadOnlyList<string> Models => (ModelsText ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(m => m.Trim())
        .Where(m => m.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>Gets or sets a value indicating whether the provider is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>
    /// Gets or sets a new API key typed into the password box, stored in the Credential Manager when the page is saved
    /// and then cleared. Empty means "keep the stored key".
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (Set(ref _apiKey, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasPendingKey));
                OnPropertyChanged(nameof(KeyStatusText));
            }
        }
    }

    /// <summary>Gets a value indicating whether a new key is waiting to be saved.</summary>
    public bool HasPendingKey => ApiKey.Length > 0;

    /// <summary>Gets a value indicating whether a key is stored in the Credential Manager (presence only).</summary>
    public bool HasStoredKey
    {
        get => _hasStoredKey;
        internal set
        {
            if (SetProperty(ref _hasStoredKey, value))
            {
                OnPropertyChanged(nameof(KeyStatusText));
            }
        }
    }

    /// <summary>Gets the key indicator text: "Key stored", "No key stored", or that a new key will be stored on save.</summary>
    public string KeyStatusText => HasPendingKey
        ? "New key will be stored when you save"
        : HasStoredKey ? "Key stored" : "No key stored";

    /// <summary>Gets the result of the last Test connection.</summary>
    public string TestResult
    {
        get => _testResult;
        internal set => SetProperty(ref _testResult, value ?? string.Empty);
    }

    /// <summary>Gets the text shown in the provider list.</summary>
    public string Title => (DisplayName.Length > 0 ? DisplayName : "(unnamed)") + (Id.Length > 0 ? " [" + Id + "]" : string.Empty);

    /// <summary>Gets the credential target the key is stored under.</summary>
    public string CredentialTarget => CredentialManagerStore.TargetPrefix + Id;

    /// <summary>Creates an item for a provider read from settings.</summary>
    /// <param name="provider">The provider's settings object.</param>
    /// <returns>The item.</returns>
    public static ProviderItemViewModel FromSettings(JObject provider)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var item = new ProviderItemViewModel((JObject)provider.DeepClone(), isNew: false);
        item.SavedId = item.Id;
        return item;
    }

    /// <summary>Creates a new, unsaved provider.</summary>
    /// <param name="id">A suggested id.</param>
    /// <returns>The item.</returns>
    public static ProviderItemViewModel CreateNew(string id) =>
        new(new JObject { ["id"] = id, ["kind"] = "openai-compatible", ["displayName"] = "New provider", ["enabled"] = true }, isNew: true);

    /// <summary>Builds the provider's settings object: the original with the edited fields replaced.</summary>
    /// <returns>The JSON object.</returns>
    public JObject ToSettings()
    {
        var result = (JObject)_original.DeepClone();
        result["id"] = Id;
        result["kind"] = Kind;
        result["displayName"] = DisplayName;
        result["endpoint"] = Endpoint;
        result["models"] = new JArray(Models.Cast<object>().ToArray());
        result["enabled"] = Enabled;
        return result;
    }

    /// <summary>Checks the item's fields.</summary>
    /// <returns>A message describing the first problem, or <see langword="null"/>.</returns>
    public string? Validate()
    {
        if (!CredentialManagerStore.IsValidName(Id))
        {
            return "Provider id '" + Id + "' must be 1 to 128 letters, digits, '.', '_' or '-'.";
        }

        if (!Kinds.Contains(Kind))
        {
            return "Provider '" + Id + "' has an unknown kind '" + Kind + "'.";
        }

        if (Endpoint.Length > 0 && !(Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            return "Provider '" + Id + "' has an endpoint that is not an http or https URL.";
        }

        return null;
    }

    /// <summary>Marks the item saved: its id is fixed and the pending key has been stored. Call on the UI thread.</summary>
    internal void MarkSaved()
    {
        IsNew = false;
        SavedId = Id;
        OnPropertyChanged(nameof(IsNew));
        _apiKey = string.Empty;
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(HasPendingKey));
        OnPropertyChanged(nameof(KeyStatusText));
    }

    private static IEnumerable<string> ReadModels(JObject provider) =>
        provider["models"] is JArray models
            ? models.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v!)
            : [];

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
