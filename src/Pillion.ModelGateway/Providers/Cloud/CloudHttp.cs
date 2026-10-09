using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Cloud;

/// <summary>Adds the credential to an outgoing request.</summary>
/// <param name="request">The request.</param>
/// <param name="cancellationToken">A token that cancels the call (an Entra ID token may need a network round trip).</param>
/// <returns><see langword="true"/> when a credential was added.</returns>
internal delegate ValueTask<bool> RequestAuthorizer(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>One server-sent event whose data parsed as JSON. Dispose it when done.</summary>
/// <param name="Name">The SSE <c>event:</c> name, or <see langword="null"/> when the stream has none (Gemini, OpenAI-style).</param>
/// <param name="Data">The parsed <c>data:</c> payload.</param>
internal sealed record SseJson(string? Name, JsonDocument Data) : IDisposable
{
    /// <inheritdoc/>
    public void Dispose() => Data.Dispose();
}

/// <summary>A probe response: the status, the body when it was JSON, and whether a credential was sent.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The parsed body of a success response, or <see langword="null"/>. Dispose it.</param>
/// <param name="CredentialSent">Whether a credential was sent.</param>
internal sealed record CloudProbeResponse(HttpStatusCode Status, JsonDocument? Body, bool CredentialSent) : IDisposable
{
    /// <inheritdoc/>
    public void Dispose() => Body?.Dispose();
}

/// <summary>
/// The HTTP plumbing the Anthropic, Gemini, Mistral and Azure OpenAI adapters share: requests that carry the credential
/// of the moment (read again for every request and never kept), JSON bodies, server-sent-event streams with or without
/// event names, and error mapping through a provider-specific mapper. Redirects are never followed (the transport
/// disables them), because one could carry the credential to another host.
/// </summary>
internal sealed partial class CloudHttp
{
    private readonly HttpClient _http;
    private readonly RequestAuthorizer _authorize;
    private readonly CloudErrorStyle _style;
    private readonly ILogger _logger;
    private bool _warnedInsecure;

    public CloudHttp(HttpClient http, Uri baseUri, string providerId, string displayName, RequestAuthorizer authorize, CloudErrorStyle style, ILogger? logger = null)
    {
        _http = http;
        BaseUri = baseUri;
        ProviderId = providerId;
        DisplayName = displayName;
        _authorize = authorize;
        _style = style;
        _logger = logger ?? NullLogger.Instance;
    }

    public Uri BaseUri { get; }

    public string ProviderId { get; }

    public string DisplayName { get; }

    /// <summary>Reads the credential for a request, refusing one with control characters (header injection) before anything is sent.</summary>
    /// <param name="key">The credential, or <see langword="null"/>.</param>
    /// <param name="providerId">The provider id for the message.</param>
    /// <returns>The credential, or <see langword="null"/> when there is none.</returns>
    /// <exception cref="ProviderException">The credential contains control characters.</exception>
    public static string? CheckKey(string? key, string providerId)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Any(char.IsControl))
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"The credential stored for '{providerId}' contains line breaks or control characters. Store it again.",
                isProviderFault: false);
        }

        return key;
    }

    /// <summary>Sends a GET and parses the JSON body.</summary>
    public async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relativeUri));
        using var response = await SendAsync(request, modelId: null, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await ReadStreamAsync(response, cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a GET for the probe and reports the response without throwing for an HTTP error status.</summary>
    public async Task<CloudProbeResponse> ProbeAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relativeUri));
        var credentialSent = await _authorize(request, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{DisplayName} at {BaseUri} did not respond in time.", innerException: ex);
        }

        using (response)
        {
            JsonDocument? body = null;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    body = null;
                }
            }

            return new CloudProbeResponse(response.StatusCode, body, credentialSent);
        }
    }

    /// <summary>
    /// Sends a POST and yields each server-sent event whose <c>data:</c> parses as JSON, until the stream ends or a
    /// <c>[DONE]</c> payload arrives. A server that ignores streaming and answers with one JSON document yields that
    /// document. A payload with an <c>error</c> property, or an event named <c>error</c>, throws the mapped exception.
    /// </summary>
    public async IAsyncEnumerable<SseJson> PostStreamAsync(string relativeUri, JsonObject body, string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, relativeUri))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));

        using var response = await SendAsync(request, modelId, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await ReadStreamAsync(response, cancellationToken).ConfigureAwait(false);

        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var whole = await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (HasError(whole.RootElement))
            {
                var text = whole.RootElement.GetRawText();
                whole.Dispose();
                throw MapMidStream(text, modelId);
            }

            yield return new SseJson(null, whole);
            yield break;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? eventName = null;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"The connection to {DisplayName} was lost while streaming.", innerException: ex);
            }

            string? payload = null;
            string? payloadName = null;
            var ended = false;
            if (line is null)
            {
                ended = true;
                if (data.Length > 0)
                {
                    payload = data.ToString();
                    payloadName = eventName;
                }
            }
            else if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    payload = data.ToString();
                    payloadName = eventName;
                }

                data.Clear();
                eventName = null;
            }
            else if (line[0] == ':')
            {
                // A comment (keep-alives).
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = TrimField(line.AsSpan(6));
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(TrimField(line.AsSpan(5)));
            }

            if (payload is not null)
            {
                if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
                {
                    yield break;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(payload);
                }
                catch (JsonException ex)
                {
                    throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{DisplayName} sent a response Pillion could not read.", innerException: ex);
                }

                if (string.Equals(payloadName, "error", StringComparison.Ordinal) || HasError(document.RootElement))
                {
                    document.Dispose();
                    throw MapMidStream(payload, modelId);
                }

                yield return new SseJson(payloadName, document);
            }

            if (ended)
            {
                yield break;
            }
        }
    }

    private static string TrimField(ReadOnlySpan<char> value) =>
        (value.Length > 0 && value[0] == ' ' ? value[1..] : value).ToString();

    private static bool HasError(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        (root.TryGetProperty("error", out _) || IsNamedError(root, "type") || IsNamedError(root, "object"));

    // Anthropic's error events have "type":"error"; Mistral's error objects have "object":"error".
    private static bool IsNamedError(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == "error";

    private ProviderException MapMidStream(string payload, string? modelId) =>
        CloudErrors.Map(HttpStatusCode.InternalServerError, payload, modelId, midStream: true, hasCredential: true, DisplayName, _style);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? modelId, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var hasCredential = await _authorize(request, cancellationToken).ConfigureAwait(false);
        if (hasCredential && !_warnedInsecure && BaseUri.Scheme == Uri.UriSchemeHttp && !BaseUri.IsLoopback)
        {
            _warnedInsecure = true;
            LogInsecure(_logger, ProviderId, BaseUri.Host);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{DisplayName} at {BaseUri} did not respond in time.", innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                text = response.ReasonPhrase ?? string.Empty;
            }

            throw CloudErrors.Map(response.StatusCode, text, modelId, midStream: false, hasCredential, DisplayName, _style);
        }
    }

    private async Task<Stream> ReadStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
    }

    private ProviderException Unreachable(HttpRequestException ex)
    {
        var refused = ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
        var tls = ex.InnerException is AuthenticationException;
        return new ProviderException(
            PillionErrorCode.ProviderUnavailable,
            refused
                ? $"{DisplayName} is not reachable at {BaseUri} (connection refused). Check the endpoint and the network."
                : tls
                    ? $"The TLS certificate of {BaseUri.Host} is not trusted. For a private certificate authority, set \"caBundle\" to its PEM file in the provider settings."
                    : $"{DisplayName} at {BaseUri} could not be reached: {ex.Message}",
            ex.StatusCode,
            ex);
    }

    private static async Task<JsonDocument> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, "The server sent a response Pillion could not read. Check the endpoint in the provider settings.", innerException: ex);
        }
    }

    [LoggerMessage(EventId = 460, Level = LogLevel.Warning, Message = "Provider '{ProviderId}' sends its credential over plain http to {Host}; use https unless the network is trusted.")]
    private static partial void LogInsecure(ILogger logger, string providerId, string host);
}
