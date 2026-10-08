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

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>
/// The HTTP plumbing shared by the OpenAI-compatible clients: bearer authentication (the key is read for each request
/// and never kept), JSON requests, server-sent-event streams and error mapping.
/// </summary>
internal sealed partial class OpenAIHttp
{
    private const int MaxServerMessageLength = 200;

    private readonly HttpClient _http;
    private readonly Func<string?> _keyProvider;
    private readonly ILogger _logger;
    private bool _warnedInsecure;
    private volatile bool _streamOptionsRejected;

    public OpenAIHttp(HttpClient http, Uri endpoint, string providerId, string displayName, Func<string?> keyProvider, ILogger? logger = null)
    {
        _http = http;
        Endpoint = endpoint;
        ProviderId = providerId;
        DisplayName = displayName;
        _keyProvider = keyProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    public Uri Endpoint { get; }

    public string ProviderId { get; }

    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the server refused <c>stream_options</c>; later requests leave it out.</summary>
    public bool StreamOptionsRejected => _streamOptionsRejected;

    /// <summary>Sends a GET and parses the JSON body.</summary>
    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, path));
        using var response = await SendAsync(request, modelId: null, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await ReadStreamAsync(response, cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a GET for the probe and reports the response without throwing for an HTTP error status.</summary>
    public async Task<(HttpStatusCode Status, string? Server, JsonDocument? Body, bool KeySent)> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, path));
        var keySent = Authorize(request);
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
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{DisplayName} at {Endpoint} did not respond in time.", innerException: ex);
        }

        using (response)
        {
            var server = response.Headers.Server.Count > 0 ? string.Join(' ', response.Headers.Server.Select(s => s.ToString())) : null;
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

            return (response.StatusCode, server, body, keySent);
        }
    }

    /// <summary>
    /// Sends a POST and yields each server-sent event's JSON payload until <c>[DONE]</c> or the end of the stream. A
    /// server that ignores <c>stream</c> and answers with one JSON document yields that document. A payload with an
    /// <c>error</c> property throws. A server that rejects <c>stream_options</c> gets the request again without it.
    /// </summary>
    public async IAsyncEnumerable<JsonDocument> PostStreamAsync(string path, JsonObject body, string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_streamOptionsRejected)
        {
            body.Remove("stream_options");
        }

        HttpResponseMessage response;
        try
        {
            response = await SendPostAsync(path, body, modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (body.ContainsKey("stream_options") && (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity) && ex.Message.Contains("stream_options", StringComparison.Ordinal))
        {
            _streamOptionsRejected = true;
            body.Remove("stream_options");
            response = await SendPostAsync(path, body, modelId, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            await using var stream = await ReadStreamAsync(response, cancellationToken).ConfigureAwait(false);
            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var whole = await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
                if (whole.RootElement.ValueKind == JsonValueKind.Object && whole.RootElement.TryGetProperty("error", out _))
                {
                    var text = whole.RootElement.GetRawText();
                    whole.Dispose();
                    throw MapError(HttpStatusCode.InternalServerError, text, modelId, midStream: true);
                }

                yield return whole;
                yield break;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
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
                var ended = false;
                if (line is null)
                {
                    ended = true;
                    payload = data.Length > 0 ? data.ToString() : null;
                }
                else if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        payload = data.ToString();
                        data.Clear();
                    }
                }
                else if (line[0] == ':')
                {
                    // A comment (keep-alives such as OpenRouter's processing notice).
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = line.AsSpan(5);
                    if (value.Length > 0 && value[0] == ' ')
                    {
                        value = value[1..];
                    }

                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
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

                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out _))
                    {
                        document.Dispose();
                        throw MapError(HttpStatusCode.InternalServerError, payload, modelId, midStream: true);
                    }

                    yield return document;
                }

                if (ended)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Maps an error response (status and body) to a classified exception. Never includes the API key.</summary>
    /// <param name="status">The HTTP status (500 for an error event inside a stream, unless the event carries a numeric code).</param>
    /// <param name="body">The response body; the OpenAI error shape, a plain string or other text.</param>
    /// <param name="modelId">The requested model, if any.</param>
    /// <param name="midStream">Whether the error arrived inside a stream.</param>
    /// <param name="hasKey">Whether a key was sent; shapes the message for 401 and 403.</param>
    /// <param name="providerName">The provider name for messages.</param>
    /// <returns>The exception to throw.</returns>
    public static ProviderException MapError(HttpStatusCode status, string body, string? modelId, bool midStream = false, bool hasKey = true, string providerName = "The model server")
    {
        var (message, code) = ParseError(body);
        if (midStream && code is { } numeric && int.TryParse(numeric, out var asStatus) && asStatus is >= 400 and < 600)
        {
            status = (HttpStatusCode)asStatus;
        }

        var lower = (message + " " + code).ToLowerInvariant();
        var statusNumber = (int)status;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The server's text is not repeated: some servers echo a masked copy of the key.
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                hasKey
                    ? $"{providerName} rejected the API key or denied access (HTTP {statusNumber}). Check the key stored for this provider."
                    : $"{providerName} requires an API key (HTTP {statusNumber}) and none is stored for this provider.",
                status,
                isProviderFault: false);
        }

        if (IsModelNotFound(status, lower, code))
        {
            return new ProviderException(
                PillionErrorCode.ModelNotInstalled,
                $"The model '{modelId}' is not available from {providerName}. Choose a model the provider lists.",
                status,
                isProviderFault: false);
        }

        if (IsContextOverflow(lower))
        {
            return new ProviderException(
                PillionErrorCode.ContextOverflow,
                $"The request does not fit in the context window of '{modelId}'. Remove some context and try again.",
                status,
                isProviderFault: false);
        }

        if (statusNumber == 429)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                lower.Contains("insufficient_quota", StringComparison.Ordinal)
                    ? $"{providerName} reports that the quota is used up (HTTP 429). Check the plan and billing."
                    : $"{providerName} is rate limiting requests (HTTP 429).",
                status);
        }

        if (statusNumber is >= 300 and < 400)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} answered with a redirect (HTTP {statusNumber}), which Pillion does not follow. Use the final endpoint URL.",
                status,
                isProviderFault: false);
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} returned HTTP 404. Check that the endpoint includes the API version, for example https://host/v1.",
                status,
                isProviderFault: false);
        }

        var detail = Truncate(message);
        var text = midStream
            ? $"{providerName} stopped with an error{(detail.Length > 0 ? ": " + detail : ".")}"
            : $"{providerName} returned HTTP {statusNumber}{(detail.Length > 0 ? ": " + detail : ".")}";
        return new ProviderException(PillionErrorCode.ProviderUnavailable, text, status);
    }

    private static bool IsModelNotFound(HttpStatusCode status, string lower, string? code) =>
        string.Equals(code, "model_not_found", StringComparison.OrdinalIgnoreCase) ||
        (status == HttpStatusCode.NotFound && lower.Contains("model", StringComparison.Ordinal));

    private static bool IsContextOverflow(string lower) =>
        lower.Contains("context_length_exceeded", StringComparison.Ordinal) ||
        lower.Contains("maximum context length", StringComparison.Ordinal) ||
        lower.Contains("prompt is too long", StringComparison.Ordinal) ||
        lower.Contains("input is too long", StringComparison.Ordinal) ||
        lower.Contains("too many tokens", StringComparison.Ordinal) ||
        lower.Contains("reduce the length", StringComparison.Ordinal) ||
        (lower.Contains("context", StringComparison.Ordinal) &&
            (lower.Contains("exceed", StringComparison.Ordinal) || lower.Contains("too long", StringComparison.Ordinal) || lower.Contains("too large", StringComparison.Ordinal) || lower.Contains("maximum", StringComparison.Ordinal)));

    /// <summary>Reads the message and code from the common error shapes.</summary>
    internal static (string Message, string? Code) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (body.Trim(), null);
            }

            var error = root.TryGetProperty("error", out var e) ? e : root;
            if (error.ValueKind == JsonValueKind.String)
            {
                return (error.GetString() ?? string.Empty, null);
            }

            if (error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
                string? code = null;
                if (error.TryGetProperty("code", out var c))
                {
                    code = c.ValueKind switch
                    {
                        JsonValueKind.String => c.GetString(),
                        JsonValueKind.Number => c.GetRawText(),
                        _ => null,
                    };
                }

                if (message.Length == 0 && error.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    message = detail.GetString() ?? string.Empty;
                }

                if (code is null && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    code = type.GetString();
                }

                return (message, code);
            }

            return (string.Empty, null);
        }
        catch (JsonException)
        {
            return (body.Trim(), null);
        }
    }

    private static string Truncate(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxServerMessageLength ? single : single[..MaxServerMessageLength] + "...";
    }

    private async Task<HttpResponseMessage> SendPostAsync(string path, JsonObject body, string modelId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        return await SendAsync(request, modelId, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? modelId, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var hasKey = Authorize(request);

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
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{DisplayName} at {Endpoint} did not respond in time.", innerException: ex);
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

            throw MapError(response.StatusCode, text, modelId, hasKey: hasKey, providerName: DisplayName);
        }
    }

    /// <summary>Adds the bearer header from the key read right now. The key lives only in this call.</summary>
    /// <returns><see langword="true"/> when a key was sent.</returns>
    private bool Authorize(HttpRequestMessage request)
    {
        var key = _keyProvider();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Any(char.IsControl))
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"The API key stored for '{ProviderId}' contains line breaks or control characters. Store it again.",
                isProviderFault: false);
        }

        if (!_warnedInsecure && Endpoint.Scheme == Uri.UriSchemeHttp && !Endpoint.IsLoopback)
        {
            _warnedInsecure = true;
            LogInsecure(_logger, ProviderId, Endpoint.Host);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return true;
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
                ? $"{DisplayName} is not reachable at {Endpoint} (connection refused). Check that the server is running."
                : tls
                    ? $"The TLS certificate of {Endpoint.Host} is not trusted. For a private certificate authority, set \"caBundle\" to its PEM file in the provider settings."
                    : $"{DisplayName} at {Endpoint} could not be reached: {ex.Message}",
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
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, "The server sent a response Pillion could not read. Check that the endpoint is an OpenAI-compatible API.", innerException: ex);
        }
    }

    [LoggerMessage(EventId = 450, Level = LogLevel.Warning, Message = "Provider '{ProviderId}' sends its API key over plain http to {Host}; use https unless the network is trusted.")]
    private static partial void LogInsecure(ILogger logger, string providerId, string host);
}
