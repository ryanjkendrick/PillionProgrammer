namespace Pillion.Common.Secrets;

/// <summary>A secret could not be read or stored. The message never contains a secret value.</summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    public SecretStoreException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
