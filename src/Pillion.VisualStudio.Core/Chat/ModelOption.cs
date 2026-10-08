using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One entry of the chat window's model picker.</summary>
public sealed class ModelOption
{
    /// <summary>The group name of models that run on this machine.</summary>
    public const string LocalGroup = "Local";

    /// <summary>The group name of cloud models.</summary>
    public const string CloudGroup = "Cloud";

    /// <summary>Initializes a new instance of the <see cref="ModelOption"/> class.</summary>
    /// <param name="descriptor">The model as the host described it.</param>
    /// <param name="providerName">The provider's display name.</param>
    /// <param name="unavailableReason">Why the model cannot be used now, or <see langword="null"/> when it can.</param>
    public ModelOption(ModelDescriptor descriptor, string? providerName, string? unavailableReason)
    {
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        Id = descriptor.Id ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? Id : descriptor.DisplayName;
        ProviderId = descriptor.ProviderId ?? string.Empty;
        ProviderName = string.IsNullOrWhiteSpace(providerName) ? ProviderId : providerName!;
        IsLocal = descriptor.IsLocal;
        UnavailableReason = unavailableReason;
    }

    /// <summary>Gets the model id.</summary>
    public string Id { get; }

    /// <summary>Gets the model's display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the id of the provider that serves the model.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the provider's display name (from the providers setting), or its id.</summary>
    public string ProviderName { get; }

    /// <summary>Gets a value indicating whether the model runs on this machine.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets <see cref="LocalGroup"/> or <see cref="CloudGroup"/>.</summary>
    public string Group => IsLocal ? LocalGroup : CloudGroup;

    /// <summary>Gets why the model cannot be used now, or <see langword="null"/> when it can.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Gets a value indicating whether the model can be picked.</summary>
    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Gets the text shown in the picker.</summary>
    public string DisplayText => DisplayName + " (" + ProviderName + ")";

    /// <summary>Gets the reply header naming the model and provider, for example "llama3.2 via Ollama, local".</summary>
    public string ReplyHeader => DisplayName + " via " + ProviderName + (IsLocal ? ", local" : ", cloud");

    /// <summary>Gets the name a screen reader announces for the entry, including why it is unavailable.</summary>
    public string AutomationName => string.Format(
        CultureInfo.CurrentCulture,
        "{0}, {1} model{2}",
        DisplayText,
        IsLocal ? "local" : "cloud",
        IsAvailable ? string.Empty : ", unavailable: " + UnavailableReason);

    /// <summary>Gets the tooltip: the model id, and the reason when unavailable.</summary>
    public string ToolTip => IsAvailable ? Id : Id + Environment.NewLine + UnavailableReason;

    /// <inheritdoc/>
    public override string ToString() => DisplayText;

    /// <summary>
    /// Builds the picker entries from the host's model list: local models first, then cloud, each sorted by name.
    /// Models without the chat capability are left out, and models the settings rule out are marked unavailable
    /// with the reason.
    /// </summary>
    /// <param name="models">The models from <see cref="IModelService.ListModelsAsync"/>.</param>
    /// <param name="policy">What the settings allow, or <see langword="null"/> for <see cref="ChatModelPolicy.Default"/>.</param>
    /// <returns>The entries, grouped and ordered.</returns>
    public static IReadOnlyList<ModelOption> Build(IEnumerable<ModelDescriptor>? models, ChatModelPolicy? policy)
    {
        var rules = policy ?? ChatModelPolicy.Default;
        return (models ?? [])
            .Where(m => m is not null && !string.IsNullOrEmpty(m.Id))
            .Where(m => m.Capabilities == ModelCapabilities.None || (m.Capabilities & ModelCapabilities.Chat) != 0)
            .Select(m => new ModelOption(m, rules.ProviderName(m.ProviderId), rules.UnavailableReason(m)))
            .OrderBy(o => o.IsLocal ? 0 : 1)
            .ThenBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(o => o.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// What the host's settings say about the models in the picker: Local-only mode, the chat cloud switch, and the
/// configured providers' names and enabled flags. (The model list itself has no availability field yet.)
/// </summary>
public sealed class ChatModelPolicy
{
    /// <summary>The reason shown when Local-only mode is on.</summary>
    public const string LocalOnlyReason = "Local-only mode is on (Tools > Options > Pillion Programmer > Privacy).";

    /// <summary>The reason shown when the chat cloud switch is off.</summary>
    public const string CloudChatOffReason = "Cloud chat is switched off (Tools > Options > Pillion Programmer > Privacy).";

    private readonly Dictionary<string, ProviderSummary> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ChatModelPolicy"/> class.</summary>
    /// <param name="localOnly">Whether Local-only mode is on.</param>
    /// <param name="cloudChat">Whether the chat cloud switch is on.</param>
    /// <param name="providers">The configured providers, or <see langword="null"/>.</param>
    public ChatModelPolicy(bool localOnly, bool cloudChat, IEnumerable<ProviderSummary>? providers)
    {
        LocalOnly = localOnly;
        CloudChat = cloudChat;
        foreach (var provider in providers ?? [])
        {
            if (provider is not null && !string.IsNullOrEmpty(provider.Id))
            {
                _providers[provider.Id] = provider;
            }
        }
    }

    /// <summary>Gets the policy used when the settings could not be read: cloud chat allowed, no provider names.</summary>
    public static ChatModelPolicy Default { get; } = new(localOnly: false, cloudChat: true, providers: null);

    /// <summary>Gets a value indicating whether Local-only mode is on.</summary>
    public bool LocalOnly { get; }

    /// <summary>Gets a value indicating whether the chat cloud switch is on.</summary>
    public bool CloudChat { get; }

    /// <summary>Gets a provider's display name, or its id when it has none.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The name.</returns>
    public string ProviderName(string? providerId) =>
        providerId is not null && _providers.TryGetValue(providerId, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName)
            ? p.DisplayName
            : providerId ?? string.Empty;

    /// <summary>Gets why a model cannot be used for chat now, or <see langword="null"/> when it can.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public string? UnavailableReason(ModelDescriptor model)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (_providers.TryGetValue(model.ProviderId ?? string.Empty, out var provider) && !provider.Enabled)
        {
            return "The provider " + ProviderName(model.ProviderId) + " is disabled (Tools > Options > Pillion Programmer > Providers and keys).";
        }

        if (model.IsLocal)
        {
            return null;
        }

        return LocalOnly ? LocalOnlyReason : CloudChat ? null : CloudChatOffReason;
    }
}

/// <summary>A configured provider as the chat window needs it.</summary>
public sealed class ProviderSummary
{
    /// <summary>Initializes a new instance of the <see cref="ProviderSummary"/> class.</summary>
    /// <param name="id">The provider id.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="enabled">Whether the provider is enabled.</param>
    public ProviderSummary(string id, string displayName, bool enabled)
    {
        Id = id ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Enabled = enabled;
    }

    /// <summary>Gets the provider id.</summary>
    public string Id { get; }

    /// <summary>Gets the display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the provider is enabled.</summary>
    public bool Enabled { get; }
}
