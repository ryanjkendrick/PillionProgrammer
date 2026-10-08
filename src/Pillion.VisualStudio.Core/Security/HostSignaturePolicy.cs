using System;

namespace Pillion.VisualStudio.Core;

/// <summary>The result of verifying a file's Authenticode signature with <c>WinVerifyTrust</c>.</summary>
public enum SignatureIntegrity
{
    /// <summary>The signature is intact and chains to a trusted root.</summary>
    Trusted,

    /// <summary>
    /// The signature is intact (the file has not been modified) but the certificate does not chain to a trusted root,
    /// for example a self-signed or test certificate.
    /// </summary>
    UntrustedChain,

    /// <summary>The signature is broken, the file was modified after signing, or verification failed.</summary>
    Invalid,
}

/// <summary>The Authenticode signer of a file.</summary>
public sealed class SignerInfo
{
    /// <summary>Initializes a new instance of the <see cref="SignerInfo"/> class.</summary>
    /// <param name="thumbprint">The signing certificate's SHA-1 thumbprint (hex).</param>
    /// <param name="subject">The signing certificate's subject.</param>
    /// <param name="integrity">The result of verifying the signature.</param>
    public SignerInfo(string thumbprint, string subject, SignatureIntegrity integrity)
    {
        Thumbprint = thumbprint ?? throw new ArgumentNullException(nameof(thumbprint));
        Subject = subject ?? string.Empty;
        Integrity = integrity;
    }

    /// <summary>Gets the signing certificate's thumbprint.</summary>
    public string Thumbprint { get; }

    /// <summary>Gets the signing certificate's subject.</summary>
    public string Subject { get; }

    /// <summary>Gets the result of verifying the signature.</summary>
    public SignatureIntegrity Integrity { get; }
}

/// <summary>Whether the supervisor may launch the host binary, and what to log.</summary>
public sealed class SignatureDecision
{
    private SignatureDecision(bool allowed, bool checkSkipped, string message)
    {
        Allowed = allowed;
        CheckSkipped = checkSkipped;
        Message = message;
    }

    /// <summary>Gets a value indicating whether the host may be launched.</summary>
    public bool Allowed { get; }

    /// <summary>Gets a value indicating whether the check was skipped because the extension itself is unsigned.</summary>
    public bool CheckSkipped { get; }

    /// <summary>Gets the message to log (and, when refused, the reason for Failed).</summary>
    public string Message { get; }

    internal static SignatureDecision Allow(string message) => new(allowed: true, checkSkipped: false, message);

    internal static SignatureDecision Skip(string message) => new(allowed: true, checkSkipped: true, message);

    internal static SignatureDecision Refuse(string message) => new(allowed: false, checkSkipped: false, message);
}

/// <summary>
/// Decides whether the host binary may be launched by comparing its Authenticode signer with the extension
/// assembly's own signer (design doc section 4).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Extension unsigned (every developer build): allowed, logged as
/// "unsigned development build, host signature check skipped".</item>
/// <item>Extension signed, host unsigned or its signature broken: refused.</item>
/// <item>Extension signed, host signed by a different certificate: refused.</item>
/// <item>Same certificate thumbprint and an intact signature: allowed. The chain does not have to be trusted, because
/// the host is pinned to the extension's own certificate rather than to the machine's root store.</item>
/// </list>
/// </remarks>
public static class HostSignaturePolicy
{
    /// <summary>The message logged when the extension is unsigned.</summary>
    public const string UnsignedDevelopmentBuildMessage = "unsigned development build, host signature check skipped";

    /// <summary>Applies the policy.</summary>
    /// <param name="extensionSigner">The extension assembly's signer, or <see langword="null"/> when it is unsigned.</param>
    /// <param name="hostSigner">The host executable's signer, or <see langword="null"/> when it is unsigned.</param>
    /// <returns>The decision.</returns>
    public static SignatureDecision Evaluate(SignerInfo? extensionSigner, SignerInfo? hostSigner)
    {
        if (extensionSigner is null)
        {
            return SignatureDecision.Skip(UnsignedDevelopmentBuildMessage);
        }

        if (hostSigner is null)
        {
            return SignatureDecision.Refuse(
                "Pillion Host is not signed but the extension is (signed by " + extensionSigner.Subject + "); refusing to launch it.");
        }

        if (hostSigner.Integrity == SignatureIntegrity.Invalid)
        {
            return SignatureDecision.Refuse("Pillion Host's signature is invalid (the file may have been modified); refusing to launch it.");
        }

        if (!string.Equals(NormalizeThumbprint(extensionSigner.Thumbprint), NormalizeThumbprint(hostSigner.Thumbprint), StringComparison.OrdinalIgnoreCase))
        {
            return SignatureDecision.Refuse(
                "Pillion Host is signed by a different certificate (" + hostSigner.Subject + ", " + hostSigner.Thumbprint
                + ") than the extension (" + extensionSigner.Subject + ", " + extensionSigner.Thumbprint + "); refusing to launch it.");
        }

        return SignatureDecision.Allow("Pillion Host signature verified (" + hostSigner.Subject + ", " + hostSigner.Thumbprint + ").");
    }

    private static string NormalizeThumbprint(string thumbprint) => thumbprint.Replace(" ", string.Empty).Replace(":", string.Empty);
}
