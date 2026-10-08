using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// The Model Gateway's settings, read through the same <see cref="IConnectivitySettingsProvider"/> as the connectivity
/// monitor: the user settings with the repository policies in scope applied. The router's Local-only check
/// (<see cref="EffectiveSettings.LocalOnly"/>) and the monitor's <see cref="ConnectivityState.LocalOnly"/> therefore
/// come from one source of truth.
/// </summary>
internal sealed class ConnectivityGatewaySettingsSource : IGatewaySettingsSource, IDisposable
{
    private readonly IConnectivitySettingsProvider _settings;

    /// <summary>Initializes a new instance of the <see cref="ConnectivityGatewaySettingsSource"/> class.</summary>
    /// <param name="settings">The effective settings provider the connectivity monitor also uses.</param>
    public ConnectivityGatewaySettingsSource(IConnectivitySettingsProvider settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _settings.Changed += OnChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public EffectiveSettings GetCurrent() => _settings.GetEffectiveSettings();

    /// <inheritdoc/>
    public void Dispose() => _settings.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
