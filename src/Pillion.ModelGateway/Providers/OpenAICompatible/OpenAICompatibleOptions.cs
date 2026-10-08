using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pillion.Common.Settings;

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>How a provider reaches the network: the system proxy, an explicit proxy, or none.</summary>
public enum ProxyMode
{
    /// <summary>Use the system proxy (the default).</summary>
    System,

    /// <summary>Use the proxy at <see cref="OpenAICompatibleOptions.ProxyUri"/>.</summary>
    Explicit,

    /// <summary>Connect directly, ignoring any system proxy.</summary>
    None,
}

/// <summary>
/// Per-provider options of the OpenAI-compatible adapter, read from the provider's <c>providers</c> entry. Besides the
/// standard <c>endpoint</c>, the adapter reads these optional properties (kept in the settings model's extension data,
/// so they survive a read-modify-write cycle):
/// <list type="bullet">
/// <item><description><c>"proxy"</c>: <c>"system"</c> (default), <c>"none"</c>, or an explicit proxy URL.</description></item>
/// <item><description><c>"caBundle"</c>: path of a PEM file with extra root certificates for enterprise gateways.</description></item>
/// <item><description><c>"supportsFim"</c>: <see langword="true"/> when a local server implements <c>POST /v1/completions</c> with <c>suffix</c>.</description></item>
/// </list>
/// </summary>
public sealed class OpenAICompatibleOptions
{
    /// <summary>The default endpoint of the <c>openai</c> kind.</summary>
    public static readonly Uri OpenAiEndpoint = new("https://api.openai.com/v1/");

    /// <summary>Gets the base endpoint, always ending with <c>/</c> and including the version segment (for example <c>https://host/v1/</c>).</summary>
    public Uri Endpoint { get; private set; } = OpenAiEndpoint;

    /// <summary>Gets the provider kind the options were read for.</summary>
    public ProviderKind Kind { get; private set; } = ProviderKind.OpenAiCompatible;

    /// <summary>Gets how the provider reaches the network.</summary>
    public ProxyMode Proxy { get; private set; }

    /// <summary>Gets the explicit proxy when <see cref="Proxy"/> is <see cref="ProxyMode.Explicit"/>.</summary>
    public Uri? ProxyUri { get; private set; }

    /// <summary>Gets the path of the PEM file with extra root certificates, or <see langword="null"/>.</summary>
    public string? CaBundlePath { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the server implements <c>POST /v1/completions</c> with <c>suffix</c>. Only honoured
    /// when the endpoint is on this machine.
    /// </summary>
    public bool SupportsFim { get; private set; }

    /// <summary>Gets a value indicating whether the endpoint is a loopback address (a local server).</summary>
    public bool IsLoopback => Endpoint.IsLoopback;

    /// <summary>Reads the endpoint of a provider entry without reading the other options.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <returns>The endpoint, or <see langword="null"/> when none is configured and the kind has no default.</returns>
    /// <exception cref="UriFormatException">The endpoint is not an absolute http or https URL.</exception>
    public static Uri? ReadEndpoint(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return settings.Kind == ProviderKind.OpenAi ? OpenAiEndpoint : null;
        }

        var text = settings.Endpoint.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException($"The endpoint '{settings.Endpoint}' is not an http or https URL.");
        }

        // A bare host means the usual /v1 root; anything with a path is taken as given (OpenRouter uses /api/v1).
        var path = parsed.AbsolutePath;
        var builder = new UriBuilder(parsed)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = path is "/" or "" ? "/v1/" : path.EndsWith('/') ? path : path + "/",
        };
        return builder.Uri;
    }

    /// <summary>Reads the options from a provider's settings.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <returns>The options.</returns>
    /// <exception cref="UriFormatException">The endpoint or the proxy is not an absolute http or https URL.</exception>
    /// <exception cref="InvalidOperationException">The kind needs an endpoint and none is set.</exception>
    public static OpenAICompatibleOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var endpoint = ReadEndpoint(settings)
            ?? throw new InvalidOperationException("An OpenAI-compatible provider needs an endpoint, for example http://localhost:1234/v1.");
        var options = new OpenAICompatibleOptions { Endpoint = endpoint, Kind = settings.Kind };

        var extension = settings.ExtensionData;
        if (extension is null)
        {
            return options;
        }

        if (extension.TryGetValue("proxy", out var proxy) && proxy.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(proxy.GetString()))
        {
            var text = proxy.GetString()!.Trim();
            if (string.Equals(text, "system", StringComparison.OrdinalIgnoreCase))
            {
                options.Proxy = ProxyMode.System;
            }
            else if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                options.Proxy = ProxyMode.None;
            }
            else if (Uri.TryCreate(text, UriKind.Absolute, out var proxyUri) && (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
            {
                options.Proxy = ProxyMode.Explicit;
                options.ProxyUri = proxyUri;
            }
            else
            {
                throw new UriFormatException("The proxy must be \"system\", \"none\" or an http or https URL.");
            }
        }

        if (extension.TryGetValue("caBundle", out var bundle) && bundle.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(bundle.GetString()))
        {
            options.CaBundlePath = bundle.GetString()!.Trim();
        }

        if (extension.TryGetValue("supportsFim", out var fim) && fim.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            options.SupportsFim = fim.GetBoolean();
        }

        return options;
    }

    /// <summary>Loads the extra root certificates from <see cref="CaBundlePath"/>.</summary>
    /// <returns>The certificates, or <see langword="null"/> when no bundle is configured. The caller disposes them.</returns>
    /// <exception cref="InvalidOperationException">The file cannot be read or holds no certificate.</exception>
    public X509Certificate2Collection? LoadCaBundle()
    {
        if (CaBundlePath is null)
        {
            return null;
        }

        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPemFile(CaBundlePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"The CA bundle '{CaBundlePath}' could not be read: {ex.Message}", ex);
        }

        if (collection.Count == 0)
        {
            throw new InvalidOperationException($"The CA bundle '{CaBundlePath}' contains no PEM certificate.");
        }

        return collection;
    }
}
