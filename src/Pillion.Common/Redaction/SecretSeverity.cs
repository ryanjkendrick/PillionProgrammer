namespace Pillion.Common.Redaction;

/// <summary>How damaging it would be if a matched secret reached a third party.</summary>
public enum SecretSeverity
{
    /// <summary>Intended to be public or of limited use on its own (for example a Stripe publishable key).</summary>
    Low = 1,

    /// <summary>Likely a credential, but either test-only, short-lived or detected heuristically.</summary>
    Medium = 2,

    /// <summary>A live credential or private key material.</summary>
    High = 3,
}
