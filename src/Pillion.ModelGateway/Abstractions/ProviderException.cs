using System.Net;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// A provider adapter's failure, classified so the gateway can decide whether to retry, fall back, or count it against
/// the provider's circuit breaker. Messages are user-facing and never contain prompt text or credentials.
/// </summary>
public class ProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    public ProviderException()
        : this(PillionErrorCode.ProviderUnavailable, "The model provider failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">A user-facing message.</param>
    public ProviderException(string message)
        : this(PillionErrorCode.ProviderUnavailable, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">A user-facing message.</param>
    /// <param name="innerException">The cause.</param>
    public ProviderException(string message, Exception? innerException)
        : this(PillionErrorCode.ProviderUnavailable, message, statusCode: null, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="code">The error code reported to the client.</param>
    /// <param name="message">A user-facing message.</param>
    /// <param name="statusCode">The HTTP status the provider returned, if any.</param>
    /// <param name="innerException">The cause.</param>
    /// <param name="isProviderFault">
    /// Whether the failure is evidence that the provider is unhealthy (connection failures, timeouts, 429 and 5xx).
    /// Defaults to <see langword="true"/> for <see cref="PillionErrorCode.ProviderUnavailable"/> without a 4xx status.
    /// </param>
    /// <param name="isRetryable">
    /// Whether the same request may be sent again; defaults to the status rule (429 and 5xx). Set it for a failure without a
    /// status that is transient all the same, such as tool-call arguments that were not valid JSON.
    /// </param>
    public ProviderException(
        PillionErrorCode code,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null,
        bool? isProviderFault = null,
        bool? isRetryable = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        IsProviderFault = isProviderFault ?? (code == PillionErrorCode.ProviderUnavailable && !IsClientError(statusCode));
        _isRetryable = isRetryable;
    }

    private readonly bool? _isRetryable;

    /// <summary>Gets the error code reported to the client.</summary>
    public PillionErrorCode Code { get; }

    /// <summary>Gets the HTTP status the provider returned, if any.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets a value indicating whether the failure counts against the provider's circuit breaker.</summary>
    public bool IsProviderFault { get; }

    /// <summary>Gets a value indicating whether the request may be retried: HTTP 429 and 5xx, or what the constructor said.</summary>
    public bool IsRetryable => _isRetryable ?? (StatusCode is { } status && ((int)status == 429 || (int)status >= 500));

    /// <summary>Converts the failure to the structured error sent over RPC.</summary>
    /// <returns>The error.</returns>
    public PillionError ToError() => new()
    {
        Code = Code,
        Message = Message,
        Details = StatusCode is { } status ? $"HTTP {(int)status}" : null,
    };

    private static bool IsClientError(HttpStatusCode? status) =>
        status is { } s && (int)s is >= 400 and < 500 && (int)s != 429 && (int)s != 408;
}
