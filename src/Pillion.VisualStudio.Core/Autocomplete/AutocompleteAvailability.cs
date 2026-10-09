using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Follows the autocomplete feature flag and settings, read from Pillion Host the way <see cref="ChatAvailability"/>
/// reads <c>chat</c>: <c>ISettingsService.GetAsync("effective.features")</c> for the <c>autocomplete</c> flag (an absent
/// key means on) and <c>GetAsync("autocomplete")</c> for the section. Autocomplete is available with the defaults until
/// the host says otherwise; the last answer is kept while the host restarts and refreshed when it is ready again, and
/// when the Tools menu opens.
/// </summary>
public sealed class AutocompleteAvailability : IDisposable
{
    private readonly IHostConnection _connection;
    private readonly PillionLog? _log;
    private int _version;
    private AutocompleteSettings _settings = AutocompleteSettings.Default;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteAvailability"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public AutocompleteAvailability(IHostConnection connection, PillionLog? log = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _log = log;
    }

    /// <summary>Raised, on any thread, when <see cref="Settings"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the current settings. Safe to read on any thread.</summary>
    public AutocompleteSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Starts following the host: the settings are read every time the host becomes ready.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _connection.StateChanged += OnHostStateChanged;
        _connection.SettingsChanged += OnHostSettingsChanged;
        if (IsUsable(_connection.State))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    /// <summary>Reads the flag and the section from the host. Never throws (except for cancellation); a failure keeps the last answer.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>A task that completes when the answer has been applied.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsUsable(_connection.State))
        {
            return;
        }

        var version = Interlocked.Increment(ref _version);
        AutocompleteSettings settings;
        try
        {
            var service = _connection.GetProxy<ISettingsService>();
            var features = await service.GetAsync(ChatAvailability.EffectiveFeaturesKey, cancellationToken).ConfigureAwait(false);
            var section = await service.GetAsync(AutocompleteSettings.SectionKey, cancellationToken).ConfigureAwait(false);
            settings = AutocompleteSettings.FromJson(AutocompleteSettings.ReadFeatureFlag(features), section);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without an answer the settings stay as they were; the host being away is normal during a restart.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("autocomplete.settings.unavailable", "Could not read the autocomplete settings.", ("error", ex.GetType().Name));
            return;
        }

        if (version != Volatile.Read(ref _version))
        {
            return; // A newer read is on its way.
        }

        Apply(settings);
    }

    /// <summary>Replaces the settings (tests, and the host's answer).</summary>
    /// <param name="settings">The new settings.</param>
    public void Apply(AutocompleteSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var previous = Interlocked.Exchange(ref _settings, settings);
        if (previous.Enabled != settings.Enabled)
        {
            _log?.Info("autocomplete.features.changed", settings.Enabled ? "Autocomplete is enabled." : "Autocomplete is disabled by settings or policy.");
        }

        if (previous.Enabled != settings.Enabled
            || previous.AnnounceSuggestions != settings.AnnounceSuggestions
            || !SameLanguages(previous, settings))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _connection.StateChanged -= OnHostStateChanged;
            _connection.SettingsChanged -= OnHostSettingsChanged;
        }
    }

    private static bool IsUsable(HostState state) => state is HostState.Ready or HostState.Degraded;

    private static bool SameLanguages(AutocompleteSettings a, AutocompleteSettings b)
    {
        if (a.DisabledLanguages.Count != b.DisabledLanguages.Count)
        {
            return false;
        }

        foreach (var language in a.DisabledLanguages)
        {
            if (!SuppressionRules.IsLanguageDisabled(language, b.DisabledLanguages))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Raised on a background thread: reads the flag and the section again when the settings they come from changed.</summary>
    private void OnHostSettingsChanged(object? sender, HostSettingsChangedEventArgs e)
    {
        if (e.Affects("features", AutocompleteSettings.SectionKey))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (IsUsable(e.State) && !IsUsable(e.PreviousState))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }
}
