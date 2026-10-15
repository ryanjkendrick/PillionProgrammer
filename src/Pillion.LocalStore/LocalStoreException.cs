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

    /// <summary>A read-only open found no database file (its owner has not created it yet, or it was deleted).</summary>
    DatabaseMissing,

    /// <summary>A read-only open found a database with an older schema than this build reads; only its owner migrates it.</summary>
    NeedsMigration,
}

/// <summary>
/// A Local Store failure. It is a <see cref="PillionRpcException"/>, so it can cross the RPC boundary as is, and its
/// message is written for the user. The precise reason is <see cref="Kind"/>.
/// </summary>
/// <remarks>
/// Every kind uses <see cref="PillionErrorCode.StorageUnavailable"/> (contract 1.3), except
/// <see cref="LocalStoreErrorKind.FeatureDisabled"/>, which uses <see cref="PillionErrorCode.PolicyDenied"/> as every other
/// feature flag that is off does. Before contract 1.3 every kind used <see cref="PillionErrorCode.PolicyDenied"/>. The
/// message and <see cref="PillionError.Details"/> never contain key material.
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
                Code = CodeFor(kind),
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

    /// <summary>Gets the contract error code a failure of a kind is reported with.</summary>
    /// <param name="kind">Why the store failed.</param>
    /// <returns><see cref="PillionErrorCode.PolicyDenied"/> for <see cref="LocalStoreErrorKind.FeatureDisabled"/>; otherwise <see cref="PillionErrorCode.StorageUnavailable"/>.</returns>
    public static PillionErrorCode CodeFor(LocalStoreErrorKind kind) =>
        kind == LocalStoreErrorKind.FeatureDisabled ? PillionErrorCode.PolicyDenied : PillionErrorCode.StorageUnavailable;
}
