using Pillion.Common.Settings;

namespace Pillion.ModelGateway;

/// <summary>Supplies the settings the gateway runs with, after repository policies are applied.</summary>
public interface IGatewaySettingsSource
{
    /// <summary>Raised when the settings may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current effective settings.</summary>
    /// <returns>The settings.</returns>
    EffectiveSettings GetCurrent();
}

/// <summary>
/// Reads settings from the <see cref="SettingsStore"/>. Repository policies are not known to the gateway yet (the
/// workspace-trust work wires them in), so the user's settings are resolved with no policies.
/// </summary>
public sealed class SettingsStoreGatewaySettingsSource : IGatewaySettingsSource, IDisposable
{
    private readonly SettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="SettingsStoreGatewaySettingsSource"/> class.</summary>
    /// <param name="store">The settings store. It is not loaded until the first <see cref="GetCurrent"/>.</param>
    public SettingsStoreGatewaySettingsSource(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Changed += OnStoreChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public EffectiveSettings GetCurrent() => EffectiveSettings.Resolve(_store.GetSettings(), policies: null);

    /// <inheritdoc/>
    public void Dispose() => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged(object? sender, SettingsChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A fixed <see cref="IGatewaySettingsSource"/>, for tests and tools.</summary>
public sealed class StaticGatewaySettingsSource : IGatewaySettingsSource
{
    private EffectiveSettings _current;

    /// <summary>Initializes a new instance of the <see cref="StaticGatewaySettingsSource"/> class.</summary>
    /// <param name="settings">The user settings.</param>
    public StaticGatewaySettingsSource(PillionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = EffectiveSettings.Resolve(settings, policies: null);
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public EffectiveSettings GetCurrent() => _current;

    /// <summary>Replaces the settings and raises <see cref="Changed"/>.</summary>
    /// <param name="settings">The new user settings.</param>
    public void Update(PillionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = EffectiveSettings.Resolve(settings, policies: null);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
