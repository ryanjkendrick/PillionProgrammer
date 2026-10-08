using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Pillion.Common.Connectivity;

/// <summary>
/// Probes an origin with one HTTP <c>GET /</c>. Any HTTP response, including 401, 403 and 404, means the origin is
/// reachable; DNS, connection, TLS and proxy failures and timeouts mean it is not. No credentials, request body or
/// user data are sent. The system proxy is used by default, with the current user's credentials for the proxy only.
/// </summary>
public sealed class HttpProviderProber : IProviderProber, IDisposable
{
    /// <summary>The default time a probe may take, covering DNS, connect, TLS and the response headers.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a new instance of the <see cref="HttpProviderProber"/> class.</summary>
    /// <param name="timeout">The time one probe may take, or <see langword="null"/> for <see cref="DefaultTimeout"/>.</param>
    /// <param name="handler">The HTTP handler to use, or <see langword="null"/> for a handler that follows the system proxy.</param>
    public HttpProviderProber(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            ConnectTimeout = _timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Origin);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PillionProgrammer", "0"));
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return ProbeResult.Success(stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Failure(ProbeErrorCategory.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return ProbeResult.Failure(Categorize(ex));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            return ProbeResult.Failure(ProbeErrorCategory.Other);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    private static ProbeErrorCategory Categorize(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => ProbeErrorCategory.Dns,
        HttpRequestError.ConnectionError => ProbeErrorCategory.Connect,
        HttpRequestError.SecureConnectionError => ProbeErrorCategory.Tls,
        HttpRequestError.ProxyTunnelError => ProbeErrorCategory.Proxy,
        _ => ProbeErrorCategory.Other,
    };
}
