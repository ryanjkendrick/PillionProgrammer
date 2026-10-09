using System.Net;
using System.Text.Json;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.OpenAICompatible;

namespace Pillion.ModelGateway.Providers.Cloud;

/// <summary>
/// What the cloud adapters (Anthropic, Gemini, Mistral, Azure OpenAI) share outside the request loop: the endpoint, the
/// network options (proxy and CA bundle, read from the provider entry exactly like the OpenAI-compatible adapter) and
/// the secret lookup.
/// </summary>
internal static class CloudTransport
{
    /// <summary>The placeholder endpoint used to run the OpenAI-compatible option reader for the proxy and CA bundle only.</summary>
    private const string PlaceholderEndpoint = "https://placeholder.invalid/";

    /// <summary>Resolves a provider's base endpoint.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="defaultEndpoint">The endpoint used when the entry sets none, or <see langword="null"/> when the kind has no default.</param>
    /// <param name="bareHostPath">The path to use when the endpoint is a bare host (Mistral: <c>/v1/</c>), or <see langword="null"/> to keep the root.</param>
    /// <returns>An absolute http or https URI ending with <c>/</c>, without query or fragment.</returns>
    /// <exception cref="InvalidOperationException">No endpoint is set and the kind has no default.</exception>
    /// <exception cref="UriFormatException">The endpoint is not an absolute http or https URL.</exception>
    public static Uri ResolveEndpoint(ProviderSettings settings, Uri? defaultEndpoint, string? bareHostPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return defaultEndpoint
                ?? throw new InvalidOperationException($"A {ProviderKinds.ToName(settings.Kind)} provider needs an endpoint, for example https://your-resource.openai.azure.com.");
        }

        var text = settings.Endpoint.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException($"The endpoint '{settings.Endpoint}' is not an http or https URL.");
        }

        var path = parsed.AbsolutePath;
        if (path is "/" or "")
        {
            path = bareHostPath ?? "/";
        }

        return new UriBuilder(parsed)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = path.EndsWith('/') ? path : path + "/",
        }.Uri;
    }

    /// <summary>Creates the HTTP client of a provider: the system or explicit proxy and the optional CA bundle.</summary>
    /// <param name="settings">The provider settings (the proxy and CA bundle come from its extension data).</param>
    /// <param name="handlerFactory">Creates the handler (tests pass a fake), or <see langword="null"/> for the real one.</param>
    /// <param name="owned">Something to dispose with the client (the CA bundle certificates), or <see langword="null"/>.</param>
    /// <returns>The client; the caller disposes it.</returns>
    /// <exception cref="UriFormatException">The proxy is not valid.</exception>
    /// <exception cref="InvalidOperationException">The CA bundle cannot be read.</exception>
    public static HttpClient CreateClient(ProviderSettings settings, Func<HttpMessageHandler>? handlerFactory, out IDisposable? owned)
    {
        ArgumentNullException.ThrowIfNull(settings);

        owned = null;
        HttpMessageHandler handler;
        if (handlerFactory is not null)
        {
            handler = handlerFactory();
        }
        else
        {
            var options = OpenAICompatibleOptions.FromSettings(new ProviderSettings
            {
                Id = settings.Id,
                Kind = settings.Kind,
                Endpoint = PlaceholderEndpoint,
                ExtensionData = settings.ExtensionData,
            });
            handler = OpenAITransport.CreateHandler(options, out var roots);
            owned = roots is null ? null : new DisposableCertificates(roots);
        }

        // Streaming responses can take minutes; the gateway applies its own time limits.
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Reads a provider's secret, treating an unreadable store as "no key".</summary>
    /// <param name="secrets">The secret store.</param>
    /// <param name="providerId">The provider id (the secret name).</param>
    /// <returns>The key, or <see langword="null"/>.</returns>
    public static string? ReadKey(ISecretStore secrets, string providerId)
    {
        try
        {
            return secrets.Get(providerId);
        }
        catch (Exception ex) when (ex is ArgumentException or SecretStoreException)
        {
            return null;
        }
    }

    /// <summary>Reads a string property from the provider entry's extension data.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when absent or empty.</returns>
    public static string? ReadString(ProviderSettings settings, string name)
    {
        if (settings.ExtensionData is { } data &&
            data.TryGetValue(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        return null;
    }

    /// <summary>Combines the outcome of a probe request into the result Test connection shows.</summary>
    /// <param name="response">The probe response.</param>
    /// <param name="displayName">The provider name.</param>
    /// <param name="endpoint">The endpoint for the message.</param>
    /// <param name="credentialName">What the credential is called.</param>
    /// <param name="countModels">Reads the number of models from a success body, or <see langword="null"/> when the body is not a model list.</param>
    /// <returns>The result.</returns>
    public static CloudProbeResult Interpret(CloudProbeResponse response, string displayName, Uri endpoint, string credentialName, Func<JsonElement, int?>? countModels)
    {
        ArgumentNullException.ThrowIfNull(response);

        var number = (int)response.Status;
        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var message = response.CredentialSent
                ? $"Reached {endpoint}, but it rejected the {credentialName} (HTTP {number}). Check the credentials for this provider."
                : $"Reached {endpoint}, but it requires a {credentialName} (HTTP {number}) and none was sent.";
            return new CloudProbeResult(true, false, null, message);
        }

        if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return new CloudProbeResult(
                true,
                null,
                null,
                $"Reached {endpoint}, but it has no model list (HTTP {number}), so the {credentialName} could not be verified. Check the endpoint in the provider settings.");
        }

        if (number is < 200 or >= 300)
        {
            return new CloudProbeResult(false, null, null, $"{displayName} at {endpoint} answered HTTP {number}.");
        }

        int? count = response.Body is { } body && countModels is not null ? countModels(body.RootElement) : null;
        var models = count is { } c ? $" {c} model{(c == 1 ? string.Empty : "s")} listed." : string.Empty;
        return new CloudProbeResult(true, response.CredentialSent ? true : null, count, $"Connected to {endpoint}; the {credentialName} was accepted.{models}");
    }
}

/// <summary>What <c>ProbeDetailedAsync</c> of a cloud provider learned.</summary>
/// <param name="IsReachable">Whether the service answered at all.</param>
/// <param name="IsAuthenticated"><see langword="true"/> when a credential was sent and accepted, <see langword="false"/> when it was rejected or missing, <see langword="null"/> when it could not be verified.</param>
/// <param name="ModelCount">The number of models listed, or <see langword="null"/>.</param>
/// <param name="Message">A user-facing description.</param>
public sealed record CloudProbeResult(bool IsReachable, bool? IsAuthenticated, int? ModelCount, string Message)
{
    /// <summary>Converts the result to the gateway's probe result: a rejected credential counts as not usable.</summary>
    /// <returns>The probe result.</returns>
    public ProviderProbeResult ToProbeResult() => new(IsReachable && IsAuthenticated != false, null, Message);

    /// <summary>Creates the result for a failed attempt to ask the service.</summary>
    /// <param name="message">The user-facing message.</param>
    /// <returns>An unreachable result.</returns>
    internal static CloudProbeResult Unreachable(string message) => new(false, null, null, message);
}
