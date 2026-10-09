using System.Globalization;
using System.Net;
using System.Text.Json;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Cloud;

/// <summary>The message, code and numeric status found in a provider's error body.</summary>
/// <param name="Message">The provider's message (may be empty).</param>
/// <param name="Code">A machine-readable code or type (for example <c>not_found_error</c>, <c>NOT_FOUND</c>), or <see langword="null"/>.</param>
/// <param name="Status">A numeric HTTP-like status carried in the body (Google's <c>error.code</c>), or <see langword="null"/>.</param>
internal sealed record CloudErrorBody(string Message, string? Code, int? Status);

/// <summary>What differs between providers when an HTTP error is mapped to a <see cref="ProviderException"/>.</summary>
internal sealed record CloudErrorStyle
{
    /// <summary>Gets what the credential is called in messages ("API key", "Entra ID token").</summary>
    public string CredentialName { get; init; } = "API key";

    /// <summary>Gets a value indicating whether a 404 on a request that names a model means the model does not exist.</summary>
    public bool NotFoundMeansModel { get; init; } = true;

    /// <summary>Gets a test on the lower-cased message and code that recognises a rejected credential reported with an unusual status (Gemini sends 400).</summary>
    public Func<string, bool>? IsCredentialRejected { get; init; }

    /// <summary>Gets a test on the lower-cased message and code that recognises a model that does not exist.</summary>
    public Func<string, bool>? IsModelMissing { get; init; }
}

/// <summary>
/// Maps HTTP errors of the cloud providers to classified <see cref="ProviderException"/>s. Server text is never repeated
/// for credential errors (some servers echo a masked key) and never includes prompt text.
/// </summary>
internal static class CloudErrors
{
    private const int MaxServerMessageLength = 200;

    /// <summary>Reads the message, code and status from the error shapes of Anthropic, Google, OpenAI-style and Mistral bodies.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>What was found; empty when the body is not recognisable.</returns>
    public static CloudErrorBody Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new CloudErrorBody(string.Empty, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CloudErrorBody(body.Trim(), null, null);
            }

            var error = root.TryGetProperty("error", out var nested) ? nested : root;
            if (error.ValueKind == JsonValueKind.String)
            {
                return new CloudErrorBody(error.GetString() ?? string.Empty, null, null);
            }

            if (error.ValueKind != JsonValueKind.Object)
            {
                return new CloudErrorBody(string.Empty, null, null);
            }

            var message = ReadString(error, "message") ?? string.Empty;
            if (message.Length == 0)
            {
                message = ReadDetail(error);
            }

            int? status = null;
            string? code = null;
            if (error.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var number))
                {
                    status = number;
                }
                else if (codeElement.ValueKind == JsonValueKind.String)
                {
                    code = codeElement.GetString();
                    if (int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        status = parsed;
                        code = null;
                    }
                }
            }

            // Google puts the symbolic name in "status"; Anthropic and Mistral use "type".
            code ??= ReadString(error, "status") ?? ReadString(error, "type");
            return new CloudErrorBody(message, code, status);
        }
        catch (JsonException)
        {
            return new CloudErrorBody(body.Trim(), null, null);
        }
    }

    /// <summary>Maps a failed response (or an error event inside a stream) to an exception.</summary>
    /// <param name="status">The HTTP status; for an error event, the status the event implies (500 when unknown).</param>
    /// <param name="body">The response body or event payload.</param>
    /// <param name="modelId">The requested model, or <see langword="null"/> for requests that name none (listing, probing).</param>
    /// <param name="midStream">Whether the error arrived inside a stream.</param>
    /// <param name="hasCredential">Whether a credential was sent.</param>
    /// <param name="providerName">The provider name for messages.</param>
    /// <param name="style">Provider-specific recognition rules.</param>
    /// <returns>The exception to throw.</returns>
    public static ProviderException Map(
        HttpStatusCode status,
        string body,
        string? modelId,
        bool midStream,
        bool hasCredential,
        string providerName,
        CloudErrorStyle style)
    {
        var parsed = Parse(body);
        if (midStream)
        {
            status = parsed.Status is { } bodyStatus and >= 400 and < 600
                ? (HttpStatusCode)bodyStatus
                : StatusForEvent(parsed.Code, status);
        }

        var lower = (parsed.Message + " " + parsed.Code).ToLowerInvariant();
        var number = (int)status;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || style.IsCredentialRejected?.Invoke(lower) == true)
        {
            // The server's text is not repeated: some servers echo a masked copy of the credential.
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                hasCredential
                    ? $"{providerName} rejected the {style.CredentialName} or denied access (HTTP {number}). Check the credentials for this provider."
                    : $"{providerName} requires a {style.CredentialName} (HTTP {number}) and none was sent.",
                status,
                isProviderFault: false);
        }

        if (number == 402 || lower.Contains("credit balance", StringComparison.Ordinal))
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} reports a billing problem (HTTP {number}). Check the plan and payment details.",
                status,
                isProviderFault: false);
        }

        if (modelId is not null &&
            ((status == HttpStatusCode.NotFound && style.NotFoundMeansModel) ||
             lower.Contains("model_not_found", StringComparison.Ordinal) ||
             style.IsModelMissing?.Invoke(lower) == true))
        {
            return new ProviderException(
                PillionErrorCode.ModelNotInstalled,
                $"The model '{modelId}' is not available from {providerName}. Choose a model the provider lists.",
                status,
                isProviderFault: false);
        }

        if (IsContextOverflow(lower) || number == 413)
        {
            return new ProviderException(
                PillionErrorCode.ContextOverflow,
                $"The request does not fit in the context window of '{modelId}'. Remove some context and try again.",
                status,
                isProviderFault: false);
        }

        if (number == 429)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                lower.Contains("quota", StringComparison.Ordinal) || lower.Contains("spend", StringComparison.Ordinal)
                    ? $"{providerName} reports that a quota or spend limit is used up (HTTP 429). Check the plan and billing."
                    : $"{providerName} is rate limiting requests (HTTP 429).",
                status);
        }

        if (number is 529 or 503)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} is overloaded or unavailable (HTTP {number}).",
                status);
        }

        if (number is >= 300 and < 400)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} answered with a redirect (HTTP {number}), which Pillion does not follow. Use the final endpoint URL.",
                status,
                isProviderFault: false);
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{providerName} returned HTTP 404. Check the endpoint URL in the provider settings.",
                status,
                isProviderFault: false);
        }

        var detail = Truncate(parsed.Message);
        var text = midStream
            ? $"{providerName} stopped with an error{(detail.Length > 0 ? ": " + detail : ".")}"
            : $"{providerName} returned HTTP {number}{(detail.Length > 0 ? ": " + detail : ".")}";
        return new ProviderException(PillionErrorCode.ProviderUnavailable, text, status);
    }

    /// <summary>Maps the type of an in-stream error event to the HTTP status the same error has outside a stream.</summary>
    /// <param name="type">The event's error type (Anthropic) or status string (Google).</param>
    /// <param name="fallback">The status to use when the type is not known.</param>
    /// <returns>The status.</returns>
    public static HttpStatusCode StatusForEvent(string? type, HttpStatusCode fallback) => type switch
    {
        "overloaded_error" => (HttpStatusCode)529,
        "rate_limit_error" or "RESOURCE_EXHAUSTED" => (HttpStatusCode)429,
        "api_error" or "INTERNAL" => HttpStatusCode.InternalServerError,
        "UNAVAILABLE" => HttpStatusCode.ServiceUnavailable,
        "timeout_error" or "DEADLINE_EXCEEDED" => HttpStatusCode.GatewayTimeout,
        "authentication_error" or "UNAUTHENTICATED" => HttpStatusCode.Unauthorized,
        "permission_error" or "PERMISSION_DENIED" => HttpStatusCode.Forbidden,
        "not_found_error" or "NOT_FOUND" => HttpStatusCode.NotFound,
        "invalid_request_error" or "INVALID_ARGUMENT" => HttpStatusCode.BadRequest,
        _ => fallback,
    };

    /// <summary>Recognises the wording providers use when a prompt does not fit the model.</summary>
    /// <param name="lower">The lower-cased message and code.</param>
    /// <returns><see langword="true"/> for a context-length error.</returns>
    public static bool IsContextOverflow(string lower) =>
        lower.Contains("context_length_exceeded", StringComparison.Ordinal) ||
        lower.Contains("maximum context length", StringComparison.Ordinal) ||
        lower.Contains("prompt is too long", StringComparison.Ordinal) ||
        lower.Contains("input is too long", StringComparison.Ordinal) ||
        lower.Contains("request_too_large", StringComparison.Ordinal) ||
        lower.Contains("too many tokens", StringComparison.Ordinal) ||
        lower.Contains("exceeds the maximum number of tokens", StringComparison.Ordinal) ||
        lower.Contains("input token count", StringComparison.Ordinal) ||
        lower.Contains("reduce the length", StringComparison.Ordinal) ||
        (lower.Contains("context", StringComparison.Ordinal) &&
            (lower.Contains("exceed", StringComparison.Ordinal) || lower.Contains("too long", StringComparison.Ordinal) || lower.Contains("too large", StringComparison.Ordinal) || lower.Contains("maximum", StringComparison.Ordinal)));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReadDetail(JsonElement error)
    {
        // Mistral's 422 validation errors: {"detail":[{"msg":"..."}]}.
        if (!error.TryGetProperty("detail", out var detail))
        {
            return string.Empty;
        }

        if (detail.ValueKind == JsonValueKind.String)
        {
            return detail.GetString() ?? string.Empty;
        }

        if (detail.ValueKind == JsonValueKind.Array)
        {
            var parts = detail.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object ? ReadString(item, "msg") : null)
                .Where(text => !string.IsNullOrEmpty(text));
            return string.Join("; ", parts);
        }

        return string.Empty;
    }

    private static string Truncate(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxServerMessageLength ? single : single[..MaxServerMessageLength] + "...";
    }
}
