using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>
/// Builds the HTTP handler of an OpenAI-compatible provider: the system or an explicit proxy, and an optional custom CA
/// bundle for enterprise gateways. Certificate validation is never switched off.
/// </summary>
internal static class OpenAITransport
{
    /// <summary>Creates the handler for a provider's options.</summary>
    /// <param name="options">The options.</param>
    /// <param name="extraRoots">The CA bundle loaded for the handler, or <see langword="null"/>; the caller disposes it with the handler.</param>
    /// <returns>The handler.</returns>
    public static HttpClientHandler CreateHandler(OpenAICompatibleOptions options, out X509Certificate2Collection? extraRoots)
    {
        ArgumentNullException.ThrowIfNull(options);

        extraRoots = options.LoadCaBundle();
        var handler = new HttpClientHandler
        {
            // Never follow redirects: one could carry the Authorization header to another host.
            AllowAutoRedirect = false,
        };

        switch (options.Proxy)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Explicit:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(options.ProxyUri!) { BypassProxyOnLocal = true, Credentials = CredentialCache.DefaultCredentials };
                break;
            default:
                // The system proxy (HttpClient's default); authenticating proxies get the signed-in user's credentials.
                handler.UseProxy = true;
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }

        if (extraRoots is { } roots)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => ValidateWithExtraRoots(certificate, chain, errors, roots);
        }

        return handler;
    }

    /// <summary>
    /// Decides whether to accept a server certificate given an extra set of trusted roots. A certificate the platform
    /// already trusts is accepted. When the only problem is that the chain does not end in a trusted root, the chain is
    /// built again with the extra roots as the only trust anchors and accepted only if that succeeds. Any other problem,
    /// in particular a host name mismatch, rejects the certificate. Revocation is not checked on the second pass because
    /// private CAs seldom publish a reachable CRL; validity dates, signatures and the trust anchor are still verified.
    /// </summary>
    /// <param name="certificate">The server's certificate.</param>
    /// <param name="chain">The chain the platform built.</param>
    /// <param name="errors">The platform's verdict.</param>
    /// <param name="extraRoots">The extra trusted roots.</param>
    /// <returns><see langword="true"/> to accept the certificate.</returns>
    internal static bool ValidateWithExtraRoots(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors, X509Certificate2Collection extraRoots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null)
        {
            return false;
        }

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(extraRoots);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
        {
            // Intermediates the server sent.
            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(certificate);
    }
}

/// <summary>Disposes the certificates of a CA bundle together with the provider that loaded them.</summary>
internal sealed class DisposableCertificates : IDisposable
{
    private readonly X509Certificate2Collection _certificates;

    public DisposableCertificates(X509Certificate2Collection certificates) => _certificates = certificates;

    public void Dispose()
    {
        foreach (var certificate in _certificates)
        {
            certificate.Dispose();
        }
    }
}
