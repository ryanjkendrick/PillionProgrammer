using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Ollama;

/// <summary>The HTTP plumbing shared by the Ollama clients: JSON requests, NDJSON streams and error mapping.</summary>
internal sealed class OllamaHttp
{
    private readonly HttpClient _http;

    public OllamaHttp(HttpClient http, Uri endpoint)
    {
        _http = http;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    /// <summary>Sends a GET and parses the JSON body.</summary>
    public async Task<JsonDocument> GetJsonAsync(string path, string? modelId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, path));
        using var response = await SendAsync(request, modelId, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a POST with a JSON body and parses the JSON response.</summary>
    public async Task<JsonDocument> PostJsonAsync(string path, JsonObject body, string? modelId, CancellationToken cancellationToken)
    {
        using var request = CreatePost(path, body);
        using var response = await SendAsync(request, modelId, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a POST and yields each NDJSON line of the streamed response. A line with an <c>error</c> property throws.</summary>
    public async IAsyncEnumerable<JsonDocument> PostStreamAsync(string path, JsonObject body, string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreatePost(path, body);
        using var response = await SendAsync(request, modelId, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await ReadStreamAsync(response, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(PillionErrorCode.ProviderUnavailable, "The connection to Ollama was lost while streaming.", innerException: ex);
            }

            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new ProviderException(PillionErrorCode.ProviderUnavailable, "Ollama sent a response Pillion could not read.", innerException: ex);
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.GetString() ?? "unknown error";
                document.Dispose();
                throw MapError(HttpStatusCode.InternalServerError, message, modelId, midStream: true);
            }

            yield return document;
        }
    }

    /// <summary>Maps an Ollama error message and status to a classified exception.</summary>
    public static ProviderException MapError(HttpStatusCode status, string message, string? modelId, bool midStream = false)
    {
        var lower = message.ToLowerInvariant();
        if (status == HttpStatusCode.NotFound || (lower.Contains("not found", StringComparison.Ordinal) && lower.Contains("model", StringComparison.Ordinal)))
        {
            return new ProviderException(
                PillionErrorCode.ModelNotInstalled,
                $"The model '{modelId}' is not installed in Ollama. Run 'ollama pull {modelId}' to install it.",
                status,
                isProviderFault: false);
        }

        if ((lower.Contains("context", StringComparison.Ordinal) && (lower.Contains("exceed", StringComparison.Ordinal) || lower.Contains("length", StringComparison.Ordinal) || lower.Contains("too long", StringComparison.Ordinal)))
            || lower.Contains("prompt is too long", StringComparison.Ordinal)
            || lower.Contains("input length exceeds", StringComparison.Ordinal))
        {
            return new ProviderException(
                PillionErrorCode.ContextOverflow,
                $"The request does not fit in the context window of '{modelId}'. Remove some context and try again.",
                status,
                isProviderFault: false);
        }

        if (lower.Contains("does not support insert", StringComparison.Ordinal))
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"The model '{modelId}' does not support fill-in-the-middle completion.",
                status,
                isProviderFault: false);
        }

        return new ProviderException(
            PillionErrorCode.ProviderUnavailable,
            midStream ? $"Ollama stopped with an error: {message}" : $"Ollama returned HTTP {(int)status}: {message}",
            status);
    }

    private static HttpRequestMessage CreatePost(string path, JsonObject body) => new(HttpMethod.Post, new Uri(path, UriKind.Relative))
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
    };

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? modelId, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.IsAbsoluteUri)
        {
            request.RequestUri = new Uri(Endpoint, request.RequestUri);
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
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"Ollama at {Endpoint} did not respond in time.", innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string message;
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                message = TryReadError(text) ?? response.ReasonPhrase ?? "error";
            }
            catch (HttpRequestException)
            {
                message = response.ReasonPhrase ?? "error";
            }

            throw MapError(response.StatusCode, message, modelId);
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
        return new ProviderException(
            PillionErrorCode.ProviderUnavailable,
            refused
                ? $"Ollama is not running at {Endpoint} (connection refused). Start Ollama and try again."
                : $"Ollama at {Endpoint} could not be reached: {ex.Message}",
            ex.StatusCode,
            ex);
    }

    private static string? TryReadError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }

    private static async Task<JsonDocument> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, "Ollama sent a response Pillion could not read.", innerException: ex);
        }
    }
}
