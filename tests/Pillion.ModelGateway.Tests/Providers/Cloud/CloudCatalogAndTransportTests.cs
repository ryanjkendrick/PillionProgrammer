using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Providers.Cloud;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Providers.Mistral;

namespace Pillion.ModelGateway.Tests.Providers.Cloud;

/// <summary>The catalog entries and the shared transport of the four cloud adapters.</summary>
public sealed class CloudCatalogAndTransportTests
{
    private static readonly ModelCatalog Catalog = ModelCatalog.Default;

    [Theory]
    [InlineData(ProviderKind.Anthropic, "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5")]
    [InlineData(ProviderKind.Gemini, "gemini-3.5-flash-lite", "gemini-3.8-flash", "gemini-3.1-pro-preview")]
    [InlineData(ProviderKind.Mistral, "codestral-latest", "mistral-small-latest", "mistral-medium-3-5")]
    public void TheCatalog_NamesAFastBalancedAndFrontierModelPerCloudKind(ProviderKind kind, string fast, string balanced, string frontier)
    {
        Assert.Equal(fast, Catalog.GetDefaultModel(kind, Profile.Fast));
        Assert.Equal(balanced, Catalog.GetDefaultModel(kind, Profile.Balanced));
        Assert.Equal(frontier, Catalog.GetDefaultModel(kind, Profile.Frontier));
        Assert.Null(Catalog.GetDefaultModel(kind, Profile.LocalFast));
    }

    [Fact]
    public void AzureOpenAi_HasNoCatalogDefaults_BecauseDeploymentNamesAreChosenByTheUser()
    {
        Assert.All(new[] { Profile.Fast, Profile.Balanced, Profile.Frontier }, p => Assert.Null(Catalog.GetDefaultModel(ProviderKind.AzureOpenAi, p)));
    }

    [Theory]
    [InlineData("claude-fable-5-1", 10, 50)]
    [InlineData("claude-opus-5-5", 4, 20)]
    [InlineData("claude-sonnet-5-5", 2, 10)]
    [InlineData("claude-haiku-5-5", 0.10, 0.50)]
    public void TheClaudeFamilies_CarryTheDocumentedWindowLimitsAndPrices(string model, double input, double output)
    {
        var capabilities = Catalog.GetCapabilities(model, isLocal: false);

        Assert.Equal(1_000_000, capabilities.ContextWindow);
        Assert.Equal(128_000, capabilities.MaxOutputTokens);
        Assert.True(capabilities.Chat);
        Assert.True(capabilities.Vision);
        Assert.True(capabilities.PromptCaching);
        Assert.True(capabilities.ReasoningEffort);
        Assert.Equal(ToolCallingSupport.Native, capabilities.ToolCalling);
        Assert.False(capabilities.Fim);
        Assert.Equal((decimal)input, capabilities.InputCostPerMillionTokens);
        Assert.Equal((decimal)output, capabilities.OutputCostPerMillionTokens);
    }

    [Theory]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-3.5-flash-lite", true)]
    [InlineData("gemini-3.1-flash-lite", true)]
    [InlineData("gemini-3.1-pro-preview", true)]
    [InlineData("gemini-2.5-pro", false)]
    public void TheGeminiFamilies_CarryTheDocumentedWindowAndWhetherThinkingLevelsApply(string model, bool effort)
    {
        var capabilities = Catalog.GetCapabilities(model, isLocal: false);

        Assert.Equal(1_048_576, capabilities.ContextWindow);
        Assert.Equal(65_536, capabilities.MaxOutputTokens);
        Assert.True(capabilities.Vision);
        Assert.Equal(effort, capabilities.ReasoningEffort);
        Assert.True(capabilities.InputCostPerMillionTokens > 0);
        Assert.True(capabilities.OutputCostPerMillionTokens > capabilities.InputCostPerMillionTokens);
    }

    [Fact]
    public void Codestral_IsMarkedFim_AndOtherMistralModelsAreNot()
    {
        Assert.True(Catalog.GetCapabilities("codestral-latest", isLocal: false).Fim);
        Assert.True(Catalog.GetCapabilities("codestral-2508", isLocal: false).Fim);
        Assert.Equal(128_000, Catalog.GetCapabilities("codestral-latest", isLocal: false).ContextWindow);
        Assert.False(Catalog.GetCapabilities("mistral-small-latest", isLocal: false).Fim);
        Assert.False(Catalog.GetCapabilities("mistral-medium-3-5", isLocal: false).Fim);
        Assert.Equal(256_000, Catalog.GetCapabilities("mistral-medium-3-5", isLocal: false).ContextWindow);
    }

    [Fact]
    public void ClaudeModelsOutsideTheNamedFamilies_KeepTheGenericDescriptor_WithoutTheEffortControl()
    {
        var capabilities = Catalog.GetCapabilities("anthropic/claude-3-7-sonnet", isLocal: false);

        Assert.False(capabilities.ReasoningEffort);
        Assert.Equal(200_000, capabilities.ContextWindow);
    }

    [Fact]
    public void TheCloudFactories_NameTheirFeatureFlags_AndRequireAKeyWhereExpected()
    {
        var secrets = new InMemorySecretStore();

        Assert.Equal(FeatureNames.AnthropicProvider, new AnthropicProviderFactory(secrets).FeatureFlag);
        Assert.Equal(FeatureNames.GeminiProvider, new GeminiProviderFactory(secrets).FeatureFlag);
        Assert.Equal(FeatureNames.MistralProvider, new MistralProviderFactory(secrets).FeatureFlag);
        Assert.Equal(FeatureNames.AzureOpenAIProvider, new AzureOpenAIProviderFactory(secrets).FeatureFlag);
        Assert.All(
            new IModelProviderFactory[] { new AnthropicProviderFactory(secrets), new GeminiProviderFactory(secrets), new MistralProviderFactory(secrets), new AzureOpenAIProviderFactory(secrets) },
            f => Assert.True(f.RequiresApiKey(new ProviderSettings { Id = "x", Kind = f.Kind, Endpoint = "https://x.example" })));
    }

    [Fact]
    public void TheFlagDefaults_AreOn_AndAPolicyCanSwitchThemOff()
    {
        var features = new FeatureSettings();
        var names = new[] { FeatureNames.AnthropicProvider, FeatureNames.GeminiProvider, FeatureNames.MistralProvider, FeatureNames.AzureOpenAIProvider };

        Assert.All(names, n => Assert.True(features.IsEnabled(n), n));
        Assert.Equal(["anthropicProvider", "geminiProvider", "mistralProvider", "azureOpenAIProvider"], names);
        var effective = EffectiveSettings.Resolve(new PillionSettings(), [RepositoryPolicy.Parse("""{ "disabledFeatures": ["anthropicProvider", "AzureOpenAIProvider"] }""")]);
        Assert.False(effective.IsFeatureEnabled(FeatureNames.AnthropicProvider));
        Assert.False(effective.IsFeatureEnabled(FeatureNames.AzureOpenAIProvider));
        Assert.True(effective.IsFeatureEnabled(FeatureNames.GeminiProvider));
    }

    [Theory]
    [InlineData("https://api.example.com", null, "https://api.example.com/")]
    [InlineData("https://api.example.com/", "/v1/", "https://api.example.com/v1/")]
    [InlineData("https://gw.example/anthropic", "/v1/", "https://gw.example/anthropic/")]
    [InlineData("https://gw.example/a/b/?x=1#frag", null, "https://gw.example/a/b/")]
    [InlineData("http://localhost:8080", null, "http://localhost:8080/")]
    public void ResolveEndpoint_NormalisesTheEndpoint(string endpoint, string? bareHostPath, string expected)
    {
        var uri = CloudTransport.ResolveEndpoint(new ProviderSettings { Kind = ProviderKind.Mistral, Endpoint = endpoint }, null, bareHostPath);

        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Fact]
    public void ResolveEndpoint_UsesTheDefault_RejectsOtherSchemes_AndNeedsOneWhenThereIsNoDefault()
    {
        var empty = new ProviderSettings { Kind = ProviderKind.Anthropic };

        Assert.Equal(AnthropicProvider.DefaultEndpoint, CloudTransport.ResolveEndpoint(empty, AnthropicProvider.DefaultEndpoint));
        Assert.Throws<InvalidOperationException>(() => CloudTransport.ResolveEndpoint(new ProviderSettings { Kind = ProviderKind.AzureOpenAi }, null));
        Assert.Throws<UriFormatException>(() => CloudTransport.ResolveEndpoint(new ProviderSettings { Kind = ProviderKind.Anthropic, Endpoint = "ftp://example.com" }, null));
        Assert.Throws<UriFormatException>(() => CloudTransport.ResolveEndpoint(new ProviderSettings { Kind = ProviderKind.Anthropic, Endpoint = "not a url" }, null));
    }

    [Fact]
    public void TheRealTransport_ReadsTheProxyAndCaBundleFromTheProviderEntry()
    {
        var context = new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null);
        var secrets = new InMemorySecretStore();
        var badProxy = new ProviderSettings
        {
            Id = "claude",
            Kind = ProviderKind.Anthropic,
            ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "proxy": "socks5://nope" }"""),
        };
        var missingBundle = new ProviderSettings
        {
            Id = "claude",
            Kind = ProviderKind.Anthropic,
            ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "caBundle": "C:\\does\\not\\exist.pem" }"""),
        };
        var goodProxy = new ProviderSettings
        {
            Id = "claude",
            Kind = ProviderKind.Anthropic,
            ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "proxy": "http://proxy.corp.example:3128" }"""),
        };

        // A real handler is built (nothing is sent): bad options are reported to the registry as a reason to hide the provider.
        Assert.Throws<UriFormatException>(() => new AnthropicProviderFactory(secrets).Create(badProxy, context));
        Assert.Throws<InvalidOperationException>(() => new AnthropicProviderFactory(secrets).Create(missingBundle, context));
        using var provider = new AnthropicProviderFactory(secrets).Create(goodProxy, context);
        Assert.Equal("claude", provider.Id);
    }

    [Theory]
    [InlineData(401, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"x\"}}", PillionErrorCode.ProviderUnavailable)]
    [InlineData(404, "{\"error\":{\"message\":\"model gone\"}}", PillionErrorCode.ModelNotInstalled)]
    [InlineData(400, "{\"error\":{\"message\":\"context window exceeded\"}}", PillionErrorCode.ContextOverflow)]
    [InlineData(413, "{\"type\":\"error\",\"error\":{\"type\":\"request_too_large\",\"message\":\"too big\"}}", PillionErrorCode.ContextOverflow)]
    [InlineData(500, "", PillionErrorCode.ProviderUnavailable)]
    public void Map_ClassifiesTheCommonStatuses(int status, string body, PillionErrorCode expected)
    {
        var ex = CloudErrors.Map((HttpStatusCode)status, body, "m", midStream: false, hasCredential: true, "Provider", new CloudErrorStyle());

        Assert.Equal(expected, ex.Code);
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", 529)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}", 429)]
    [InlineData("{\"error\":{\"code\":503,\"message\":\"down\",\"status\":\"UNAVAILABLE\"}}", 503)]
    [InlineData("{\"error\":{\"code\":429,\"message\":\"quota\",\"status\":\"RESOURCE_EXHAUSTED\"}}", 429)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"boom\"}}", 500)]
    public void Map_MidStreamErrors_KeepTheStatusTheSameErrorHasOutsideAStream(string payload, int expectedStatus)
    {
        var ex = CloudErrors.Map(HttpStatusCode.InternalServerError, payload, "m", midStream: true, hasCredential: true, "Provider", new CloudErrorStyle());

        Assert.Equal((HttpStatusCode)expectedStatus, ex.StatusCode);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public void Parse_ReadsTheShapesOfAllFourProviders()
    {
        Assert.Equal(new CloudErrorBody("m", "not_found_error", null), CloudErrors.Parse("""{"type":"error","error":{"type":"not_found_error","message":"m"}}"""));
        Assert.Equal(new CloudErrorBody("m", "NOT_FOUND", 404), CloudErrors.Parse("""{"error":{"code":404,"message":"m","status":"NOT_FOUND"}}"""));
        Assert.Equal(new CloudErrorBody("m", "DeploymentNotFound", null), CloudErrors.Parse("""{"error":{"code":"DeploymentNotFound","message":"m"}}"""));
        Assert.Equal(new CloudErrorBody("m", null, 401), CloudErrors.Parse("""{"error":{"code":"401","message":"m"}}"""));
        Assert.Equal(new CloudErrorBody("m", "rate_limited", null), CloudErrors.Parse("""{"object":"error","message":"m","type":"rate_limited","code":null}"""));
        Assert.Equal(new CloudErrorBody("bad; worse", null, null), CloudErrors.Parse("""{"detail":[{"msg":"bad"},{"msg":"worse"}]}"""));
        Assert.Equal(new CloudErrorBody("plain text", null, null), CloudErrors.Parse("plain text"));
        Assert.Equal(new CloudErrorBody(string.Empty, null, null), CloudErrors.Parse(string.Empty));
    }
}
