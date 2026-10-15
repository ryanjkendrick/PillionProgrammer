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

    /// <summary>The Anthropic provider.</summary>
    public const string AnthropicProvider = "anthropicProvider";

    /// <summary>The Google Gemini provider.</summary>
    public const string GeminiProvider = "geminiProvider";

    /// <summary>The Mistral provider (chat and Codestral fill-in-the-middle).</summary>
    public const string MistralProvider = "mistralProvider";

    /// <summary>The Azure OpenAI provider.</summary>
    public const string AzureOpenAIProvider = "azureOpenAIProvider";

    /// <summary>Ghost-text autocomplete: the host's completion service (<c>IModelService.CompleteAsync</c>).</summary>
    public const string Autocomplete = "autocomplete";

    /// <summary>Inline chat: the host's inline edit service (<c>IInlineEditService</c>).</summary>
    public const string InlineChat = "inlineChat";

    /// <summary>The chat window's <c>@</c>-mentions: the picker and the context pins (work item 1.9).</summary>
    public const string Mentions = "mentions";

    /// <summary>The chat window's Context Inspector and Outbound log view (work item 1.13).</summary>
    public const string OutboundLogView = "outboundLogView";

    /// <summary>The embedding gateway: <c>IModelGateway.EmbedAsync</c> and the embedding adapters (work item 2.6). It also needs <see cref="ModelGateway"/>.</summary>
    public const string Embeddings = "embeddings";

    /// <summary>The bundled embedding provider: the in-process ONNX Runtime model shipped with the extension and the models downloaded on request (work item 2.7). It also needs <see cref="Embeddings"/>.</summary>
    public const string BundledEmbeddings = "bundledEmbeddings";

    /// <summary>Gets every feature flag name, in the order they are written to <c>settings.json</c>.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Chat,
        ModelGateway,
        ConnectivityProbes,
        CloudProviders,
        OllamaProvider,
        OpenAICompatibleProvider,
        AnthropicProvider,
        GeminiProvider,
        MistralProvider,
        AzureOpenAIProvider,
        Autocomplete,
        InlineChat,
        Mentions,
        OutboundLogView,
        Embeddings,
        BundledEmbeddings,
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

    /// <summary>Gets or sets a value indicating whether the Anthropic provider is available.</summary>
    public bool AnthropicProvider { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the Google Gemini provider is available.</summary>
    public bool GeminiProvider { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the Mistral provider is available.</summary>
    public bool MistralProvider { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the Azure OpenAI provider is available.</summary>
    public bool AzureOpenAIProvider { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ghost-text autocomplete is available. When off, completion requests fail with a policy error.</summary>
    public bool Autocomplete { get; set; } = true;

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
            "anthropicprovider" => AnthropicProvider,
            "geminiprovider" => GeminiProvider,
            "mistralprovider" => MistralProvider,
            "azureopenaiprovider" => AzureOpenAIProvider,
            "autocomplete" => Autocomplete,
            "inlinechat" => InlineChat,
            "mentions" => Mentions,
            "outboundlogview" => OutboundLogView,
            "embeddings" => Embeddings,
            "bundledembeddings" => BundledEmbeddings,
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
            case "anthropicprovider":
                AnthropicProvider = false;
                break;
            case "geminiprovider":
                GeminiProvider = false;
                break;
            case "mistralprovider":
                MistralProvider = false;
                break;
            case "azureopenaiprovider":
                AzureOpenAIProvider = false;
                break;
            case "autocomplete":
                Autocomplete = false;
                break;
            case "inlinechat":
                InlineChat = false;
                break;
            case "mentions":
                Mentions = false;
                break;
            case "outboundlogview":
                OutboundLogView = false;
                break;
            case "embeddings":
                Embeddings = false;
                break;
            case "bundledembeddings":
                BundledEmbeddings = false;
                break;
        }
    }

    /// <summary>Gets or sets a value indicating whether inline chat is available. When off, inline edit requests fail with a policy error.</summary>
    public bool InlineChat { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the chat window offers <c>@</c>-mentions (the picker and the context pins).</summary>
    public bool Mentions { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the chat window shows the Context Inspector and the Outbound log, and the host answers their read methods.</summary>
    public bool OutboundLogView { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the embedding gateway answers requests. When off, <c>EmbedAsync</c> fails with a policy error. It also needs <see cref="ModelGateway"/>.</summary>
    public bool Embeddings { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the bundled embedding provider is available: the ONNX model shipped with the
    /// extension (and models downloaded on request) runs in the host, with no network. When off, the provider is hidden from routing.
    /// </summary>
    public bool BundledEmbeddings { get; set; } = true;
}
