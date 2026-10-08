namespace Pillion.Common.Connectivity;

/// <summary>Why a provider is considered unreachable.</summary>
public enum ProbeErrorCategory
{
    /// <summary>No error: the provider answered.</summary>
    None,

    /// <summary>The host name could not be resolved.</summary>
    Dns,

    /// <summary>The connection was refused, reset or could not be established.</summary>
    Connect,

    /// <summary>The TLS handshake failed (certificate or protocol error).</summary>
    Tls,

    /// <summary>The proxy refused or could not tunnel the request.</summary>
    Proxy,

    /// <summary>The provider did not answer within the probe timeout.</summary>
    Timeout,

    /// <summary>The circuit breaker reported repeated request failures.</summary>
    CircuitBreaker,

    /// <summary>Any other failure.</summary>
    Other,
}

/// <summary>Helpers for <see cref="ProbeErrorCategory"/>.</summary>
public static class ProbeErrorCategoryExtensions
{
    /// <summary>Gets a short lower-case description for logs and reason strings.</summary>
    /// <param name="category">The category.</param>
    /// <returns>For example <c>timeout</c> or <c>dns failure</c>.</returns>
    public static string ToDescription(this ProbeErrorCategory category) => category switch
    {
        ProbeErrorCategory.None => "reachable",
        ProbeErrorCategory.Dns => "dns failure",
        ProbeErrorCategory.Connect => "connection failed",
        ProbeErrorCategory.Tls => "tls failure",
        ProbeErrorCategory.Proxy => "proxy failure",
        ProbeErrorCategory.Timeout => "timeout",
        ProbeErrorCategory.CircuitBreaker => "circuit breaker open",
        _ => "error",
    };
}
