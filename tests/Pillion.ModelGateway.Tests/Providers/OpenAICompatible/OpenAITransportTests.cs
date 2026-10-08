using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.OpenAICompatible;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

/// <summary>A throwaway PKI: a root, an intermediate and a server certificate for 127.0.0.1 and localhost.</summary>
internal sealed class TestPki : IDisposable
{
    private TestPki(X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf)
    {
        Root = root;
        Intermediate = intermediate;
        Leaf = leaf;
    }

    /// <summary>Gets the root, with its private key.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>Gets the intermediate, with its private key.</summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>Gets the server certificate, with its private key.</summary>
    public X509Certificate2 Leaf { get; }

    /// <summary>Gets the root without its private key.</summary>
    public X509Certificate2 RootPublic => X509CertificateLoader.LoadCertificate(Root.RawData);

    /// <summary>Gets the server certificate without its private key, as a client sees it.</summary>
    public X509Certificate2 LeafPublic => X509CertificateLoader.LoadCertificate(Leaf.RawData);

    /// <summary>Gets the intermediate without its private key.</summary>
    public X509Certificate2 IntermediatePublic => X509CertificateLoader.LoadCertificate(Intermediate.RawData);

    public static TestPki Create(string commonName = "Pillion Test", bool expiredLeaf = false)
    {
        var now = DateTimeOffset.UtcNow;
        var root = CreateCa($"CN={commonName} Root", issuer: null, now.AddDays(-60));
        var intermediate = CreateCa($"CN={commonName} Intermediate", root, now.AddDays(-40));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var notBefore = expiredLeaf ? now.AddDays(-20) : now.AddDays(-1);
        var notAfter = expiredLeaf ? now.AddDays(-5) : now.AddDays(30);
        using var signed = request.Create(intermediate, notBefore, notAfter, NewSerial());
        var leaf = signed.CopyWithPrivateKey(key);

        // Windows needs a persisted-looking key for SslStream servers; a PKCS#12 round trip provides one.
        var usable = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), null);
        leaf.Dispose();
        return new TestPki(root, intermediate, usable);
    }

    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        Leaf.Dispose();
    }

    /// <summary>Builds the chain the platform would have built for <see cref="LeafPublic"/> (which fails to trust it), with the intermediate the server sent.</summary>
    public X509Chain PlatformChain()
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.Add(IntermediatePublic);
        chain.Build(LeafPublic);
        return chain;
    }

    private static X509Certificate2 CreateCa(string subject, X509Certificate2? issuer, DateTimeOffset now)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is null)
        {
            return request.CreateSelfSigned(now, now.AddDays(120));
        }

        using var signed = request.Create(issuer, now, now.AddDays(90), NewSerial());
        return signed.CopyWithPrivateKey(key);
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        return serial;
    }
}

public sealed class OpenAITransportTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private static ProviderSettings Settings(string endpoint, string extensionJson) => new()
    {
        Id = "gw",
        Kind = ProviderKind.OpenAiCompatible,
        Endpoint = endpoint,
        ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson),
    };

    private string WriteBundle(params X509Certificate2[] certificates)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pillion-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, string.Concat(certificates.Select(c => c.ExportCertificatePem() + "\n")));
        _files.Add(path);
        return path;
    }

    private static string Json(string path) => JsonSerializer.Serialize(new { caBundle = path });

    // Proxy.
    [Fact]
    public void Proxy_DefaultsToTheSystemProxy()
    {
        var options = OpenAICompatibleOptions.FromSettings(new ProviderSettings { Id = "gw", Endpoint = "https://gw.example/v1" });
        using var handler = OpenAITransport.CreateHandler(options, out var roots);

        Assert.Equal(ProxyMode.System, options.Proxy);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(roots);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void Proxy_AnExplicitUrl_IsApplied()
    {
        var options = OpenAICompatibleOptions.FromSettings(Settings("https://gw.example/v1", """{ "proxy": "http://proxy.corp.example:3128" }"""));
        using var handler = OpenAITransport.CreateHandler(options, out _);

        Assert.Equal(ProxyMode.Explicit, options.Proxy);
        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://proxy.corp.example:3128"), proxy.GetProxy(new Uri("https://gw.example/v1/models")));
        Assert.False(proxy.IsBypassed(new Uri("https://gw.example/v1/models")));
    }

    [Fact]
    public void Proxy_None_DisablesTheSystemProxy()
    {
        var options = OpenAICompatibleOptions.FromSettings(Settings("https://gw.example/v1", """{ "proxy": "none" }"""));
        using var handler = OpenAITransport.CreateHandler(options, out _);

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task Proxy_AnExplicitProxy_ReceivesTheRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var proxy = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            await using var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var body = """{ "object": "list", "data": [ { "id": "proxied-model" } ] }""";
            var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cts.Token);
            return received.ToString();
        });

        var secrets = new InMemorySecretStore();
        secrets.Set("gw", OpenAICompatibleProviderTests.Key);
        using var provider = new OpenAICompatibleProviderFactory(secrets).Create(
            Settings("http://openai.invalid/v1", $$"""{ "proxy": "http://127.0.0.1:{{port}}" }"""),
            new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null));

        var models = await provider.ListModelsAsync(cts.Token);
        var request = await proxy;

        Assert.Equal("proxied-model", Assert.Single(models).ModelId);
        Assert.StartsWith("GET http://openai.invalid/v1/models HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer " + OpenAICompatibleProviderTests.Key, request, StringComparison.Ordinal);
    }

    // Streaming over a real socket.
    [Fact]
    public async Task Chat_OverARealSocket_DeliversEachEventBeforeTheNextOneIsSent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var firstSeen = new SemaphoreSlim(0);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            await using var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    return;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            async Task SendChunkAsync(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:x}\r\n"), cts.Token);
                await stream.WriteAsync(bytes, cts.Token);
                await stream.WriteAsync("\r\n"u8.ToArray(), cts.Token);
                await stream.FlushAsync(cts.Token);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"), cts.Token);
            await SendChunkAsync("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"one\"}}]}\n\n");
            await firstSeen.WaitAsync(cts.Token);
            await SendChunkAsync("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" two\"}}]}\n\ndata: [DONE]\n\n");
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cts.Token);
        });

        using var provider = new OpenAICompatibleProviderFactory(new InMemorySecretStore()).Create(
            Settings($"http://127.0.0.1:{port}/v1", "{}"),
            new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null));
        var texts = new List<string>();

        await foreach (var update in provider.GetChatClient("m").GetStreamingResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")], null, cts.Token))
        {
            texts.Add(update.Text);
            firstSeen.Release();
        }

        await server;
        Assert.Equal(["one", " two"], texts);
    }

    // Custom CA bundle: the validation callback.
    [Fact]
    public void CaBundle_AChainUnderTheExtraRoot_IsAccepted_EvenThroughAnIntermediate()
    {
        using var pki = TestPki.Create();
        using var root = pki.RootPublic;
        using var leaf = pki.LeafPublic;
        using var chain = pki.PlatformChain();

        var accepted = OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors, new X509Certificate2Collection(root));

        Assert.True(accepted);
    }

    [Fact]
    public void CaBundle_AChainUnderAnotherRoot_IsRejected()
    {
        using var pki = TestPki.Create();
        using var other = TestPki.Create("Someone Else");
        using var otherRoot = other.RootPublic;
        using var leaf = pki.LeafPublic;
        using var chain = pki.PlatformChain();

        var accepted = OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors, new X509Certificate2Collection(otherRoot));

        Assert.False(accepted);
    }

    [Fact]
    public void CaBundle_ATrustedChain_IsAccepted_AndAHostNameMismatchIsNeverForgiven()
    {
        using var pki = TestPki.Create();
        using var root = pki.RootPublic;
        using var leaf = pki.LeafPublic;
        using var chain = pki.PlatformChain();

        Assert.True(OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.None, new X509Certificate2Collection(root)));
        Assert.False(OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, new X509Certificate2Collection(root)));
        Assert.False(OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch, new X509Certificate2Collection(root)));
        Assert.False(OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateNotAvailable, new X509Certificate2Collection(root)));
        Assert.False(OpenAITransport.ValidateWithExtraRoots(null, chain, SslPolicyErrors.RemoteCertificateChainErrors, new X509Certificate2Collection(root)));
    }

    [Fact]
    public void CaBundle_AnExpiredCertificateUnderTheExtraRoot_IsRejected()
    {
        using var pki = TestPki.Create(expiredLeaf: true);
        using var root = pki.RootPublic;
        using var leaf = pki.LeafPublic;
        using var chain = pki.PlatformChain();

        Assert.False(OpenAITransport.ValidateWithExtraRoots(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors, new X509Certificate2Collection(root)));
    }

    [Fact]
    public void CaBundle_IsLoadedFromPem_AndInstalledOnTheHandler()
    {
        using var pki = TestPki.Create();
        using var root = pki.RootPublic;
        using var leaf = pki.LeafPublic;
        using var chain = pki.PlatformChain();
        var path = WriteBundle(root);
        var options = OpenAICompatibleOptions.FromSettings(Settings("https://gw.example/v1", Json(path)));
        using var handler = OpenAITransport.CreateHandler(options, out var roots);

        Assert.Equal(path, options.CaBundlePath);
        Assert.Single(roots!);
        var callback = handler.ServerCertificateCustomValidationCallback;
        Assert.NotNull(callback);
        using var request = new HttpRequestMessage();
        Assert.True(callback(request, leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(callback(request, leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch));

        using var otherPki = TestPki.Create("Someone Else");
        using var otherRoot = otherPki.RootPublic;
        var otherOptions = OpenAICompatibleOptions.FromSettings(Settings("https://gw.example/v1", Json(WriteBundle(otherRoot))));
        using var otherHandler = OpenAITransport.CreateHandler(otherOptions, out _);
        Assert.False(otherHandler.ServerCertificateCustomValidationCallback!(request, leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CaBundle_AMissingOrEmptyFile_IsRejectedAtCreation()
    {
        var secrets = new InMemorySecretStore();
        var factory = new OpenAICompatibleProviderFactory(secrets);
        var context = new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null);
        var empty = Path.Combine(Path.GetTempPath(), $"pillion-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(empty, "not a certificate");
        _files.Add(empty);

        var missing = Assert.Throws<InvalidOperationException>(() => factory.Create(Settings("https://gw.example/v1", Json(Path.Combine(Path.GetTempPath(), "does-not-exist.pem"))), context));
        var garbage = Assert.Throws<InvalidOperationException>(() => factory.Create(Settings("https://gw.example/v1", Json(empty)), context));

        Assert.Contains("CA bundle", missing.Message, StringComparison.Ordinal);
        Assert.Contains("CA bundle", garbage.Message, StringComparison.Ordinal);
    }

    // Custom CA bundle: a real TLS handshake against a local server.
    [Fact]
    public async Task CaBundle_EndToEnd_TheServerIsTrustedOnlyWithTheRightBundle()
    {
        using var pki = TestPki.Create();
        using var root = pki.RootPublic;
        using var other = TestPki.Create("Someone Else");
        using var otherRoot = other.RootPublic;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeTlsAsync(listener, pki, cts.Token);

        var endpoint = $"https://127.0.0.1:{port}/v1";
        var context = new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null);
        var factory = new OpenAICompatibleProviderFactory(new InMemorySecretStore());
        using var trusting = factory.Create(Settings(endpoint, Json(WriteBundle(root))), context);
        using var wrongBundle = factory.Create(Settings(endpoint, Json(WriteBundle(otherRoot))), context);
        using var noBundle = factory.Create(Settings(endpoint, "{}"), context);

        var models = await trusting.ListModelsAsync(cts.Token);
        var wrong = await Assert.ThrowsAsync<ProviderException>(() => wrongBundle.ListModelsAsync(cts.Token));
        var none = await Assert.ThrowsAsync<ProviderException>(() => noBundle.ListModelsAsync(cts.Token));
        await cts.CancelAsync();
        await serving;

        Assert.Equal("tls-model", Assert.Single(models).ModelId);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, wrong.Code);
        Assert.Contains("caBundle", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("caBundle", none.Message, StringComparison.Ordinal);
    }

    private static async Task ServeTlsAsync(TcpListener listener, TestPki pki, CancellationToken cancellationToken)
    {
        var context = SslStreamCertificateContext.Create(pki.Leaf, new X509Certificate2Collection(pki.IntermediatePublic), offline: true);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                try
                {
                    await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }, cancellationToken);

                    var received = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await ssl.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var body = """{ "object": "list", "data": [ { "id": "tls-model" } ] }""";
                    var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken);
                    await ssl.FlushAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or SocketException)
                {
                    // A client that does not trust the certificate hangs up during the handshake.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The test is over.
        }
    }
}
