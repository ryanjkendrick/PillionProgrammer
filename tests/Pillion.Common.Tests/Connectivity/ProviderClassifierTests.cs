using Pillion.Common.Connectivity;
using Pillion.Common.Settings;

namespace Pillion.Common.Tests.Connectivity;

public sealed class ProviderClassifierTests
{
    [Theory]
    [InlineData(ProviderKind.Ollama, "http://example.com:11434", true)]
    [InlineData(ProviderKind.Ollama, "", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://localhost:1234/v1", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://LOCALHOST:1234/v1", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://127.0.0.1:8080", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://127.1.2.3:8080", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://[::1]:8080/v1", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://gpu-box.local/v1", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://llm.localhost", true)]
    [InlineData(ProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", false)]
    [InlineData(ProviderKind.OpenAiCompatible, "https://notlocal.example.com", false)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://localhost.example.com", false)]
    [InlineData(ProviderKind.OpenAiCompatible, "http://10.0.0.5:8000", false)]
    [InlineData(ProviderKind.OpenAi, "http://localhost:8080", false)]
    [InlineData(ProviderKind.Anthropic, "", false)]
    [InlineData(ProviderKind.AzureOpenAi, "https://x.openai.azure.com", false)]
    public void IsLocal_FollowsKindAndHost(ProviderKind kind, string endpoint, bool expected)
    {
        var provider = FakeSettingsProvider.Provider("p", kind, endpoint);

        Assert.Equal(expected, ProviderClassifier.IsLocal(provider));
    }

    [Theory]
    [InlineData(ProviderKind.OpenAi, "", "https://api.openai.com/")]
    [InlineData(ProviderKind.Anthropic, "", "https://api.anthropic.com/")]
    [InlineData(ProviderKind.OpenAi, "https://proxy.corp.example/openai/v1", "https://proxy.corp.example/")]
    [InlineData(ProviderKind.OpenAiCompatible, "https://llm.example.com:8443/v1?x=1", "https://llm.example.com:8443/")]
    public void TryGetProbeOrigin_KeepsOnlySchemeHostAndPort(ProviderKind kind, string endpoint, string expected)
    {
        var provider = FakeSettingsProvider.Provider("p", kind, endpoint);

        Assert.True(ProviderClassifier.TryGetProbeOrigin(provider, out var origin));
        Assert.Equal(expected, origin.AbsoluteUri);
    }

    [Theory]
    [InlineData(ProviderKind.AzureOpenAi, "")]
    [InlineData(ProviderKind.OpenAiCompatible, "")]
    [InlineData(ProviderKind.OpenAiCompatible, "not a url")]
    [InlineData(ProviderKind.OpenAiCompatible, "ftp://example.com")]
    public void TryGetProbeOrigin_RejectsMissingOrUnusableEndpoints(ProviderKind kind, string endpoint)
    {
        var provider = FakeSettingsProvider.Provider("p", kind, endpoint);

        Assert.False(ProviderClassifier.TryGetProbeOrigin(provider, out _));
    }
}
