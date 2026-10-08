using System.Collections.Concurrent;

namespace Pillion.Common.Secrets;

/// <summary>A <see cref="ISecretStore"/> that keeps secrets in memory only. Intended for tests.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    // Credential Manager target names are case-insensitive, so this store behaves the same way.
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string? Get(string name)
    {
        SecretNames.Validate(name);
        return _secrets.TryGetValue(name, out var value) ? value : null;
    }

    /// <inheritdoc/>
    public void Set(string name, string value)
    {
        SecretNames.Validate(name);
        SecretNames.ValidateValue(value);
        _secrets[name] = value;
    }

    /// <inheritdoc/>
    public bool Delete(string name)
    {
        SecretNames.Validate(name);
        return _secrets.TryRemove(name, out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> List() => _secrets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
}
