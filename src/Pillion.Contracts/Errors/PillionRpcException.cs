using System;

namespace Pillion.Contracts;

/// <summary>An exception that carries a <see cref="PillionError"/> across the RPC boundary.</summary>
public class PillionRpcException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PillionRpcException"/> class.</summary>
    /// <param name="error">The structured error.</param>
    public PillionRpcException(PillionError error)
        : this(error, innerException: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PillionRpcException"/> class.</summary>
    /// <param name="error">The structured error.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public PillionRpcException(PillionError error, Exception? innerException)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>Gets the structured error.</summary>
    public PillionError Error { get; }
}
