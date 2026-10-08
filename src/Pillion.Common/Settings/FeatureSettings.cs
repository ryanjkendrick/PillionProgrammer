namespace Pillion.Common.Settings;

/// <summary>The names of the feature flags, as they appear in <c>settings.json</c> (<c>features</c>) and in a policy's <c>disabledFeatures</c>.</summary>
public static class FeatureNames
{
    /// <summary>The chat window and its commands in Visual Studio.</summary>
    public const string Chat = "chat";

    /// <summary>The Model Gateway: every model call (chat, completion, model listing) made through the host.</summary>
    public const string ModelGateway = "modelGateway";

    /// <summary>Connectivity probes of cloud providers.</summary>
    public const string ConnectivityProbes = "connectivityProbes";

    /// <summary>Every provider that does not run on this machine.</summary>
    public const string CloudProviders = "cloudProviders";

    /// <summary>The Ollama provider.</summary>
    public const string OllamaProvider = "ollamaProvider";

    /// <summary>The OpenAI-compatible provider (and the OpenAI provider, which uses the same adapter).</summary>
    public const string OpenAICompatibleProvider = "openAICompatibleProvider";

    /// <summary>Gets every feature flag name, in the order they are written to <c>settings.json</c>.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Chat,
        ModelGateway,
        ConnectivityProbes,
        CloudProviders,
        OllamaProvider,
        OpenAICompatibleProvider,
    ];
}

/// <summary>
/// The feature flags. Every flag defaults to <see langword="true"/>: a feature is on unless the user or a repository
/// policy (<c>disabledFeatures</c>) switches it off. A policy can only switch a flag off.
/// </summary>
public sealed class FeatureSettings : SettingsNode
{
    /// <summary>Gets or sets a value indicating whether the chat window and its commands are available.</summary>
    public bool Chat { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the Model Gateway answers requests. When off, every model call fails with a policy error.</summary>
    public bool ModelGateway { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether cloud providers are probed for connectivity.</summary>
    public bool ConnectivityProbes { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether providers that do not run on this machine may be used.</summary>
    public bool CloudProviders { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the Ollama provider is available.</summary>
    public bool OllamaProvider { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the OpenAI-compatible provider is available.</summary>
    public bool OpenAICompatibleProvider { get; set; } = true;

    /// <summary>Determines whether a feature is on.</summary>
    /// <param name="feature">A name from <see cref="FeatureNames"/>; case is ignored.</param>
    /// <returns><see langword="true"/> when the feature is on. A feature this version does not know is on.</returns>
    public bool IsEnabled(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        return feature.Trim().ToLowerInvariant() switch
        {
            "chat" => Chat,
            "modelgateway" => ModelGateway,
            "connectivityprobes" => ConnectivityProbes,
            "cloudproviders" => CloudProviders,
            "ollamaprovider" => OllamaProvider,
            "openaicompatibleprovider" => OpenAICompatibleProvider,
            _ => true,
        };
    }

    /// <summary>Switches a feature off. A feature this version does not know is ignored.</summary>
    /// <param name="feature">A name from <see cref="FeatureNames"/>; case is ignored.</param>
    internal void Disable(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        switch (feature.Trim().ToLowerInvariant())
        {
            case "chat":
                Chat = false;
                break;
            case "modelgateway":
                ModelGateway = false;
                break;
            case "connectivityprobes":
                ConnectivityProbes = false;
                break;
            case "cloudproviders":
                CloudProviders = false;
                break;
            case "ollamaprovider":
                OllamaProvider = false;
                break;
            case "openaicompatibleprovider":
                OpenAICompatibleProvider = false;
                break;
        }
    }
}
