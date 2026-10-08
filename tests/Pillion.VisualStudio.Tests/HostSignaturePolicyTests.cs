using System.Diagnostics;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class HostSignaturePolicyTests
{
    private static readonly SignerInfo Publisher = new("AABBCCDDEEFF00112233445566778899AABBCCDD", "CN=Pillion Programmer", SignatureIntegrity.Trusted);

    [Fact]
    public void UnsignedExtension_SkipsTheCheck()
    {
        var decision = HostSignaturePolicy.Evaluate(extensionSigner: null, hostSigner: null);

        Assert.True(decision.Allowed);
        Assert.True(decision.CheckSkipped);
        Assert.Equal("unsigned development build, host signature check skipped", decision.Message);
    }

    [Fact]
    public void UnsignedExtension_SkipsTheCheckEvenIfTheHostIsSigned()
    {
        var decision = HostSignaturePolicy.Evaluate(extensionSigner: null, Publisher);

        Assert.True(decision.Allowed);
        Assert.True(decision.CheckSkipped);
    }

    [Fact]
    public void SignedExtension_UnsignedHost_IsRefused()
    {
        var decision = HostSignaturePolicy.Evaluate(Publisher, hostSigner: null);

        Assert.False(decision.Allowed);
        Assert.Contains("not signed", decision.Message);
    }

    [Fact]
    public void SignedExtension_HostSignedByAnotherCertificate_IsRefused()
    {
        var other = new SignerInfo("0000000000000000000000000000000000000000", "CN=Someone Else", SignatureIntegrity.Trusted);

        var decision = HostSignaturePolicy.Evaluate(Publisher, other);

        Assert.False(decision.Allowed);
        Assert.Contains("different certificate", decision.Message);
    }

    [Fact]
    public void SignedExtension_HostWithBrokenSignature_IsRefused()
    {
        var tampered = new SignerInfo(Publisher.Thumbprint, Publisher.Subject, SignatureIntegrity.Invalid);

        var decision = HostSignaturePolicy.Evaluate(Publisher, tampered);

        Assert.False(decision.Allowed);
        Assert.Contains("invalid", decision.Message);
    }

    [Theory]
    [InlineData(SignatureIntegrity.Trusted)]
    [InlineData(SignatureIntegrity.UntrustedChain)]
    public void SignedExtension_SameCertificate_IsAllowed(SignatureIntegrity integrity)
    {
        var host = new SignerInfo("aabbccddeeff00112233445566778899aabbccdd", Publisher.Subject, integrity);

        var decision = HostSignaturePolicy.Evaluate(Publisher, host);

        Assert.True(decision.Allowed);
        Assert.False(decision.CheckSkipped);
    }

    [Theory]
    [InlineData(0, SignatureIntegrity.Trusted)]
    [InlineData(unchecked((int)0x800B0109), SignatureIntegrity.UntrustedChain)]
    [InlineData(unchecked((int)0x800B010A), SignatureIntegrity.UntrustedChain)]
    [InlineData(unchecked((int)0x800B010D), SignatureIntegrity.UntrustedChain)]
    [InlineData(unchecked((int)0x800B0101), SignatureIntegrity.UntrustedChain)]
    [InlineData(unchecked((int)0x80096010), SignatureIntegrity.Invalid)] // TRUST_E_BAD_DIGEST
    [InlineData(unchecked((int)0x800B0100), SignatureIntegrity.Invalid)] // TRUST_E_NOSIGNATURE
    [InlineData(unchecked((int)0x800B0111), SignatureIntegrity.Invalid)] // TRUST_E_EXPLICIT_DISTRUST
    public void Classify_MapsWinVerifyTrustResults(int hresult, SignatureIntegrity expected) =>
        Assert.Equal(expected, AuthenticodeInspector.Classify(hresult));

    [Fact]
    public void Inspector_ReturnsNullForAnUnsignedAssembly() =>
        Assert.Null(AuthenticodeInspector.GetSigner(typeof(HostSignaturePolicy).Assembly.Location));

    [Fact]
    public void Inspector_ReadsAndVerifiesASignedExecutable()
    {
        // The test host process (testhost.net48.exe) ships Authenticode-signed by Microsoft.
        using var current = Process.GetCurrentProcess();
        var path = current.MainModule!.FileName;

        var signer = AuthenticodeInspector.GetSigner(path);

        Assert.NotNull(signer);
        Assert.Equal(40, signer!.Thumbprint.Length);
        Assert.Contains("Microsoft", signer.Subject);
        Assert.Equal(SignatureIntegrity.Trusted, signer.Integrity);
    }
}
