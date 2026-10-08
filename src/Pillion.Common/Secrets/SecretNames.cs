namespace Pillion.Common.Secrets;

/// <summary>Validation shared by the secret stores.</summary>
internal static class SecretNames
{
    /// <summary>The Credential Manager limit for a credential blob (CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512 bytes).</summary>
    public const int MaxBlobBytes = 5 * 512;

    /// <summary>Checks that a name is 1 to 128 characters of letters, digits, '.', '_' or '-'.</summary>
    /// <param name="name">The name to check.</param>
    public static void Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length is 0 or > 128)
        {
            throw new ArgumentException("A secret name must be 1 to 128 characters.", nameof(name));
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                throw new ArgumentException("A secret name may contain only letters, digits, '.', '_' and '-'.", nameof(name));
            }
        }
    }

    /// <summary>Checks a value is non-empty and fits in a credential blob once encoded as UTF-16.</summary>
    /// <param name="value">The value to check. It is never included in a message.</param>
    public static void ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            throw new SecretStoreException("A secret value must not be empty. Use Delete to remove a secret.");
        }

        var bytes = value.Length * sizeof(char);
        if (bytes > MaxBlobBytes)
        {
            throw new SecretStoreException($"The secret is too large to store: {bytes} bytes as UTF-16, but the limit is {MaxBlobBytes} bytes ({MaxBlobBytes / sizeof(char)} characters).");
        }
    }
}
