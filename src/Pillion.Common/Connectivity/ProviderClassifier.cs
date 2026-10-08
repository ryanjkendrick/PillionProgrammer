using System.Net;
using Pillion.Common.Settings;

namespace Pillion.Common.Connectivity;

/// <summary>Decides which configured providers are cloud providers and where to probe them.</summary>
public static class ProviderClassifier
{
    /// <summary>
    /// Determines whether a provider is a local one, which is never probed: Ollama, and an OpenAI-compatible server
    /// whose endpoint host is <c>localhost</c>, a loopback address (<c>127.0.0.0/8</c>, <c>::1</c>) or a name ending
    /// in <c>.local</c> or <c>.localhost</c>.
    /// </summary>
    /// <param name="provider">The provider settings.</param>
    /// <returns><see langword="true"/> when the provider runs on this machine or the local network by name.</returns>
    public static bool IsLocal(ProviderSettings provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.Kind switch
        {
            ProviderKind.Ollama => true,
            ProviderKind.OpenAiCompatible => TryGetEndpoint(provider, out var uri) && IsLocalHost(uri.Host),
            _ => false,
        };
    }

    /// <summary>Gets the origin (scheme, host and port) to probe for a provider.</summary>
    /// <param name="provider">The provider settings.</param>
    /// <param name="origin">The origin, with the path <c>/</c>.</param>
    /// <returns>
    /// <see langword="false"/> when the provider has no usable HTTP or HTTPS endpoint (for example an Azure OpenAI
    /// provider without an endpoint); such a provider is not monitored.
    /// </returns>
    public static bool TryGetProbeOrigin(ProviderSettings provider, out Uri origin)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!TryGetEndpoint(provider, out var endpoint))
        {
            origin = null!;
            return false;
        }

        origin = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return true;
    }

    /// <summary>Determines whether a host name or address refers to this machine.</summary>
    /// <param name="host">A host as returned by <see cref="Uri.Host"/>.</param>
    /// <returns><see langword="true"/> for localhost, loopback addresses and <c>.local</c> or <c>.localhost</c> names.</returns>
    public static bool IsLocalHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var name = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (name.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(name, out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetEndpoint(ProviderSettings provider, out Uri endpoint)
    {
        var text = provider.Endpoint?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = DefaultEndpoint(provider.Kind);
        }

        if (!string.IsNullOrEmpty(text)
            && Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(parsed.Host))
        {
            endpoint = parsed;
            return true;
        }

        endpoint = null!;
        return false;
    }

    private static string? DefaultEndpoint(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAi => "https://api.openai.com/",
        ProviderKind.Anthropic => "https://api.anthropic.com/",
        ProviderKind.Gemini => "https://generativelanguage.googleapis.com/",
        ProviderKind.Mistral => "https://api.mistral.ai/",
        ProviderKind.Ollama => "http://localhost:11434/",
        _ => null,
    };
}
