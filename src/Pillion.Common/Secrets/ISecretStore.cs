using System.Diagnostics.CodeAnalysis;

namespace Pillion.Common.Secrets;

/// <summary>
/// Stores secrets such as provider API keys. Secret values are never logged, never put in exception messages and
/// never written to settings files. A value returned by <see cref="Get"/> is a plain string: callers must not persist it.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Get and Set are the specified, conventional names; Pillion has no Visual Basic consumers.")]
public interface ISecretStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="name">The secret name, for example <c>openai</c>.</param>
    /// <returns>The value, or <see langword="null"/> if there is no such secret.</returns>
    string? Get(string name);

    /// <summary>Creates or replaces a secret.</summary>
    /// <param name="name">The secret name.</param>
    /// <param name="value">The value. It must not be empty.</param>
    /// <exception cref="SecretStoreException">The value cannot be stored (for example it is too large).</exception>
    void Set(string name, string value);

    /// <summary>Deletes a secret.</summary>
    /// <param name="name">The secret name.</param>
    /// <returns><see langword="true"/> if the secret existed.</returns>
    bool Delete(string name);

    /// <summary>Lists the names (never the values) of the stored secrets.</summary>
    /// <returns>The names, sorted.</returns>
    IReadOnlyList<string> List();
}
