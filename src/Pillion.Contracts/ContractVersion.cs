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

    /// <summary>
    /// The contract minor version. Minor versions may differ between the two sides. 1.1 added the model, provider,
    /// token and error fields on <see cref="ChatChunk"/> and <see cref="CompletionChunk"/>, the model id, availability,
    /// context window and digest on <see cref="ModelDescriptor"/>, and <see cref="ConnectivityState"/>. 1.2 added the
    /// caret, declaration, open-document and edit context on <see cref="CompletionRequest"/>, <see cref="IInlineEditService"/>
    /// and its DTOs, mentions, pins and project rules for context assembly, <see cref="TaskClass.Summarise"/>,
    /// <see cref="IDiagnosticsService"/> with <see cref="AutocompleteHostCounters"/> and the resident models, and the
    /// <c>settingsChanged</c> notification (see the README).
    /// </summary>
    public const int Minor = 2;

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
