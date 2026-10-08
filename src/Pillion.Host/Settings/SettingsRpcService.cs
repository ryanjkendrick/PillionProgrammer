using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.Host.Settings;

/// <summary>
/// Implements <see cref="ISettingsService"/> over <see cref="SettingsStore"/>. Keys are dotted paths such as
/// <c>privacy.localOnly</c>. Secrets are not reachable through this service: keys under <c>secrets.</c> are refused.
/// </summary>
public sealed class SettingsRpcService : ISettingsService
{
    private const string SecretsPrefix = "secrets";

    private readonly SettingsStore _store;
    private readonly IProviderTester _providerTester;

    /// <summary>Initializes a new instance of the <see cref="SettingsRpcService"/> class.</summary>
    /// <param name="store">The settings store.</param>
    /// <param name="providerTester">Tests provider connections.</param>
    public SettingsRpcService(SettingsStore store, IProviderTester providerTester)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providerTester);

        _store = store;
        _providerTester = providerTester;
    }

    /// <inheritdoc/>
    /// <exception cref="PillionRpcException">The key is under <c>secrets.</c> (<see cref="PillionErrorCode.PolicyDenied"/>).</exception>
    /// <exception cref="SettingsValidationException">The key is not a valid key path.</exception>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectSecretKey(key);
        return Task.FromResult(_store.GetJson(key));
    }

    /// <inheritdoc/>
    /// <exception cref="PillionRpcException">The key is under <c>secrets.</c> (<see cref="PillionErrorCode.PolicyDenied"/>).</exception>
    /// <exception cref="SettingsValidationException">The key or the JSON value is not valid.</exception>
    public Task SetAsync(string key, string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectSecretKey(key);
        _store.SetJson(key, json);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providerTester.TestAsync(providerId, cancellationToken);
    }

    private static void RejectSecretKey(string key)
    {
        if (key is null)
        {
            return; // Reported as an invalid key by the store.
        }

        var trimmed = key.Trim();
        if (trimmed.Equals(SecretsPrefix, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(SecretsPrefix + ".", StringComparison.OrdinalIgnoreCase))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = "Secrets cannot be read or written through the settings service.",
            });
        }
    }
}
