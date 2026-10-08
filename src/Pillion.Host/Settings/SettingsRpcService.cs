using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.Host.Settings;

/// <summary>
/// Implements <see cref="ISettingsService"/> over <see cref="SettingsStore"/>. Keys are dotted paths such as
/// <c>privacy.localOnly</c>. Secrets are not reachable through this service: keys under <c>secrets.</c> are refused.
/// The virtual key <see cref="EffectiveFeaturesKey"/> is read-only and returns the feature flags after policies.
/// </summary>
public sealed class SettingsRpcService : ISettingsService
{
    /// <summary>
    /// The read-only virtual key that returns the effective feature flags: the user settings with the
    /// repository policies applied (<c>disabledFeatures</c>), as a JSON object of booleans. The VSIX reads it to learn
    /// about policy restrictions, which it cannot see through <c>features</c>. Writing to it (or anything under
    /// <c>effective.</c>) fails with a <see cref="SettingsValidationException"/>.
    /// </summary>
    public const string EffectiveFeaturesKey = "effective.features";

    private const string SecretsPrefix = "secrets";
    private const string EffectivePrefix = "effective";

    private readonly SettingsStore _store;
    private readonly IProviderTester _providerTester;
    private readonly IConnectivitySettingsProvider? _effective;

    /// <summary>Initializes a new instance of the <see cref="SettingsRpcService"/> class.</summary>
    /// <param name="store">The settings store.</param>
    /// <param name="providerTester">Tests provider connections.</param>
    /// <param name="effective">
    /// Supplies the effective settings (user settings with repository policies). When <see langword="null"/>, the
    /// effective settings are the user settings with no policies.
    /// </param>
    public SettingsRpcService(SettingsStore store, IProviderTester providerTester, IConnectivitySettingsProvider? effective = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providerTester);

        _store = store;
        _providerTester = providerTester;
        _effective = effective;
    }

    /// <inheritdoc/>
    /// <exception cref="PillionRpcException">The key is under <c>secrets.</c> (<see cref="PillionErrorCode.PolicyDenied"/>).</exception>
    /// <exception cref="SettingsValidationException">The key is not a valid key path.</exception>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectSecretKey(key);
        if (IsEffectiveFeaturesKey(key))
        {
            var effective = _effective?.GetEffectiveSettings() ?? EffectiveSettings.Resolve(_store.GetSettings(), policies: null);
            return Task.FromResult<string?>(effective.FeaturesToJson());
        }

        return Task.FromResult(_store.GetJson(key));
    }

    /// <inheritdoc/>
    /// <exception cref="PillionRpcException">The key is under <c>secrets.</c> (<see cref="PillionErrorCode.PolicyDenied"/>).</exception>
    /// <exception cref="SettingsValidationException">The key or the JSON value is not valid.</exception>
    public Task SetAsync(string key, string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectSecretKey(key);
        if (IsEffectiveKey(key))
        {
            throw new SettingsValidationException($"The key \"{key.Trim()}\" is computed from the settings and the repository policies and is read-only.");
        }

        _store.SetJson(key, json);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providerTester.TestAsync(providerId, cancellationToken);
    }

    private static bool IsEffectiveFeaturesKey(string key) =>
        key is not null && key.Trim().Equals(EffectiveFeaturesKey, StringComparison.OrdinalIgnoreCase);

    private static bool IsEffectiveKey(string key)
    {
        var trimmed = key?.Trim();
        return trimmed is not null && (trimmed.Equals(EffectivePrefix, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(EffectivePrefix + ".", StringComparison.OrdinalIgnoreCase));
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
