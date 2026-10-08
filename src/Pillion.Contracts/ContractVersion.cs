namespace Pillion.Contracts;

/// <summary>
/// The version of the RPC contract defined in this assembly. Exchanged during the handshake so that a
/// VSIX and a host built from different commits can tell whether they can talk to each other.
/// </summary>
/// <remarks>
/// Increment <see cref="Major"/> for any breaking change (removing or changing a method, or changing a DTO field's
/// meaning, type or order). Increment <see cref="Minor"/> when only adding methods or appending DTO fields.
/// </remarks>
public static class ContractVersion
{
    /// <summary>The contract major version. Both sides must use the same major version.</summary>
    public const int Major = 1;

    /// <summary>The contract minor version. Minor versions may differ between the two sides.</summary>
    public const int Minor = 0;

    /// <summary>Gets the contract version formatted as <c>major.minor</c>.</summary>
    public static string Text => Major.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "." + Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Determines whether a peer that reports the given contract version can talk to this build.
    /// </summary>
    /// <param name="otherMajor">The peer's contract major version.</param>
    /// <param name="otherMinor">The peer's contract minor version. Any value is accepted; minors only add methods.</param>
    /// <returns><see langword="true"/> when the major versions are equal; otherwise <see langword="false"/>.</returns>
    public static bool IsCompatible(int otherMajor, int otherMinor)
    {
        _ = otherMinor;
        return otherMajor == Major;
    }
}
