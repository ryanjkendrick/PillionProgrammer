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

    /// <summary>The group name of the Auto entry.</summary>
    public const string AutoGroup = "Automatic";

    /// <summary>The picker id of the Auto entry. It is never sent to the host: Auto sends no model id.</summary>
    public const string AutoId = "(auto)";

    /// <summary>The reason shown on the Auto entry when no model can be used.</summary>
    public const string AutoUnavailableReason = "No model can be used right now; hover over a model to see why.";

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
        ModelId = string.IsNullOrWhiteSpace(descriptor.ModelId) ? Id : descriptor.ModelId;
        DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? ModelId : descriptor.DisplayName;
        ProviderId = descriptor.ProviderId ?? string.Empty;
        ProviderName = string.IsNullOrWhiteSpace(providerName) ? ProviderId : providerName!;
        IsLocal = descriptor.IsLocal;
        Digest = string.IsNullOrWhiteSpace(descriptor.Digest) ? null : descriptor.Digest;
        UnavailableReason = unavailableReason;
    }

    private ModelOption(string? unavailableReason)
    {
        Id = AutoId;
        ModelId = string.Empty;
        DisplayName = "Auto";
        ProviderId = string.Empty;
        ProviderName = string.Empty;
        IsAuto = true;
        UnavailableReason = unavailableReason;
    }

    /// <summary>Gets the picker's id for the model: <c>providerId/modelId</c> from the host, or <see cref="AutoId"/>.</summary>
    public string Id { get; }

    /// <summary>Gets the provider's own id for the model (for example <c>gemma4:e2b</c>); empty for Auto.</summary>
    public string ModelId { get; }

    /// <summary>Gets a value indicating whether this is the Auto entry, which lets Pillion Host choose the model.</summary>
    public bool IsAuto { get; }

    /// <summary>Gets the model's content digest when the provider reports one, for the tooltip.</summary>
    public string? Digest { get; }

    /// <summary>Gets the model's display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the id of the provider that serves the model.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the provider's display name (from the providers setting), or its id.</summary>
    public string ProviderName { get; }

    /// <summary>Gets a value indicating whether the model runs on this machine.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets <see cref="AutoGroup"/>, <see cref="LocalGroup"/> or <see cref="CloudGroup"/>.</summary>
    public string Group => IsAuto ? AutoGroup : IsLocal ? LocalGroup : CloudGroup;

    /// <summary>Gets why the model cannot be used now, or <see langword="null"/> when it can.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Gets a value indicating whether the model can be picked.</summary>
    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Gets the text shown in the picker.</summary>
    public string DisplayText => IsAuto ? "Auto (Pillion picks the model)" : DisplayName + " (" + ProviderName + ")";

    /// <summary>
    /// Gets the reply header naming the model and provider, for example "llama3.2 via Ollama, local". For Auto it is
    /// a placeholder until the first chunk names the model that answered.
    /// </summary>
    public string ReplyHeader => IsAuto ? "Auto (choosing a model)" : FormatReplyHeader(DisplayName, ProviderName, IsLocal);

    /// <summary>Gets the name a screen reader announces for the entry, including why it is unavailable.</summary>
    public string AutomationName => string.Format(
        CultureInfo.CurrentCulture,
        "{0}, {1}{2}",
        DisplayText,
        IsAuto ? "Pillion Host chooses a model for each message" : IsLocal ? "local model" : "cloud model",
        IsAvailable ? string.Empty : ", unavailable: " + UnavailableReason);

    /// <summary>Gets the tooltip: the model id and digest (or what Auto does), and the reason when unavailable.</summary>
    public string ToolTip
    {
        get
        {
            var text = IsAuto
                ? "Pillion Host picks a model for each message from the task, the Privacy settings and what is reachable. The reply names the model that answered."
                : Id + (Digest is null ? string.Empty : Environment.NewLine + "Digest: " + Digest);
            return IsAvailable ? text : text + Environment.NewLine + UnavailableReason;
        }
    }

    /// <summary>Formats a reply header, for example "gemma4:e2b via Ollama, local".</summary>
    /// <param name="model">The model's name.</param>
    /// <param name="provider">The provider's name.</param>
    /// <param name="isLocal">Whether the model is local, or <see langword="null"/> when not known.</param>
    /// <returns>The header.</returns>
    public static string FormatReplyHeader(string model, string provider, bool? isLocal) =>
        model + (string.IsNullOrEmpty(provider) ? string.Empty : " via " + provider)
        + (isLocal is null ? string.Empty : isLocal.Value ? ", local" : ", cloud");

    /// <summary>Creates the Auto entry; it is available when any listed model is.</summary>
    /// <param name="models">The entries from <see cref="Build"/>.</param>
    /// <returns>The Auto entry.</returns>
    public static ModelOption CreateAuto(IEnumerable<ModelOption>? models) =>
        new((models ?? []).Any(m => m is { IsAuto: false, IsAvailable: true }) ? null : AutoUnavailableReason);

    /// <summary>
    /// Gets the reply header for the model a chunk names: the listed entry with that provider and model id when there
    /// is one, otherwise the ids themselves.
    /// </summary>
    /// <param name="models">The picker's entries.</param>
    /// <param name="providerId">The chunk's provider id.</param>
    /// <param name="modelId">The chunk's model id.</param>
    /// <returns>The header.</returns>
    public static string ReplyHeaderFor(IEnumerable<ModelOption>? models, string? providerId, string modelId)
    {
        var entries = (models ?? []).Where(m => !m.IsAuto).ToList();
        var match = entries.FirstOrDefault(m =>
            string.Equals(m.ProviderId, providerId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.ReplyHeader;
        }

        var provider = entries.FirstOrDefault(m => string.Equals(m.ProviderId, providerId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return FormatReplyHeader(modelId, provider?.ProviderName ?? providerId ?? string.Empty, provider?.IsLocal);
    }

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
/// configured providers' names and enabled flags. These give a precise reason; a model the host itself lists as not
/// available (<see cref="ModelDescriptor.IsAvailable"/>) is greyed out too.
/// </summary>
public sealed class ChatModelPolicy
{
    /// <summary>The reason shown when Local-only mode is on.</summary>
    public const string LocalOnlyReason = "Local-only mode is on (Tools > Options > Pillion Programmer > Privacy).";

    /// <summary>The reason shown when the chat cloud switch is off.</summary>
    public const string CloudChatOffReason = "Cloud chat is switched off (Tools > Options > Pillion Programmer > Privacy).";

    /// <summary>The reason shown when Pillion Host lists a local model as not available.</summary>
    public const string LocalUnavailableReason = "Not available right now: the model is not installed or its provider is not running.";

    /// <summary>The reason shown when Pillion Host lists a cloud model as not available.</summary>
    public const string CloudUnavailableReason = "Not available right now: its provider cannot be reached or is over its limits.";

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

        if (!model.IsLocal)
        {
            if (LocalOnly)
            {
                return LocalOnlyReason;
            }

            if (!CloudChat)
            {
                return CloudChatOffReason;
            }
        }

        if (!model.IsAvailable)
        {
            return model.IsLocal ? LocalUnavailableReason : CloudUnavailableReason;
        }

        return null;
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
