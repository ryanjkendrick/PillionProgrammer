using Pillion.Common.Connectivity;

namespace Pillion.Common.Settings;

/// <summary>
/// Tells whether a feature is on. A feature is off when the user switched it off in <c>settings.json</c> (<c>features</c>)
/// or a repository policy lists it in <c>disabledFeatures</c>. Decisions are made per call, so a change in the settings
/// or the policies applies to the next request without a restart.
/// </summary>
public interface IFeatureFlags
{
    /// <summary>Raised when the settings or the policies changed, so a flag may have changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Determines whether a feature is on.</summary>
    /// <param name="feature">A name from <see cref="FeatureNames"/>.</param>
    /// <returns><see langword="true"/> when the feature is on; a feature this version does not know is on unless a policy disabled it.</returns>
    bool IsEnabled(string feature);
}

/// <summary>
/// An <see cref="IFeatureFlags"/> over the effective settings (user settings with repository policies applied). The
/// answer is cached until the settings or policies change.
/// </summary>
public sealed class SettingsFeatureFlags : IFeatureFlags, IDisposable
{
    private readonly IConnectivitySettingsProvider _settings;
    private volatile EffectiveSettings? _cached;

    /// <summary>Initializes a new instance of the <see cref="SettingsFeatureFlags"/> class.</summary>
    /// <param name="settings">Supplies the effective settings and says when they change.</param>
    public SettingsFeatureFlags(IConnectivitySettingsProvider settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _settings.Changed += OnChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public bool IsEnabled(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        // Settings that cannot be read throw: guessing "on" could ignore a policy, and nothing works without settings anyway.
        var effective = _cached ??= _settings.GetEffectiveSettings();

        return effective.IsFeatureEnabled(feature);
    }

    /// <inheritdoc/>
    public void Dispose() => _settings.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        _cached = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
