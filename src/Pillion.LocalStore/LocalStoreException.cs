using Pillion.Contracts;

namespace Pillion.LocalStore;

/// <summary>Why a Local Store database could not be opened or used.</summary>
public enum LocalStoreErrorKind
{
    /// <summary>The <c>localStore</c> feature flag is off, so the store is not opened.</summary>
    FeatureDisabled,

    /// <summary>The SQLite library that loaded is not SQLite3 Multiple Ciphers, so the database would not be encrypted.</summary>
    CipherUnavailable,

    /// <summary>The database exists but its key file does not.</summary>
    KeyMissing,

    /// <summary>The key file exists but cannot be read or decrypted by the current Windows user.</summary>
    KeyUnreadable,

    /// <summary>The key does not open the database (a different key, or the file is not a Pillion database).</summary>
    KeyMismatch,

    /// <summary>The database was written by a newer schema than this build can read.</summary>
    NewerSchema,

    /// <summary>A migration failed; the database was left at its previous version.</summary>
    MigrationFailed,
}

/// <summary>
/// A Local Store failure. It is a <see cref="PillionRpcException"/>, so it can cross the RPC boundary as is, and its
/// message is written for the user. The precise reason is <see cref="Kind"/>.
/// </summary>
/// <remarks>
/// The contract has no storage error code yet, so every kind uses <see cref="PillionErrorCode.PolicyDenied"/> (the store
/// refuses to open). A dedicated code is a candidate for the next contract version. The message and
/// <see cref="PillionError.Details"/> never contain key material.
/// </remarks>
public sealed class LocalStoreException : PillionRpcException
{
    /// <summary>Initializes a new instance of the <see cref="LocalStoreException"/> class.</summary>
    /// <param name="kind">Why the store failed.</param>
    /// <param name="message">A message for the user.</param>
    /// <param name="retryHint">What the user can do about it, if anything.</param>
    /// <param name="details">Diagnostic details such as the file path (never key material).</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public LocalStoreException(LocalStoreErrorKind kind, string message, string? retryHint = null, string? details = null, Exception? innerException = null)
        : base(
            new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = message,
                RetryHint = retryHint,
                Details = details,
            },
            innerException)
    {
        Kind = kind;
    }

    /// <summary>Gets why the store failed.</summary>
    public LocalStoreErrorKind Kind { get; }
}
