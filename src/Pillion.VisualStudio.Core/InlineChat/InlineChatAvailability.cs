using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>
/// Whether inline chat is available, from the host's effective feature flags (the <c>inlineChat</c> flag after the user
/// settings and repository policies are applied), read the way <see cref="ChatAvailability"/> reads <c>chat</c>.
/// Inline chat is available until the host says otherwise; the last answer is kept while the host restarts and read
/// again when it is ready, when the host reports a change to the <c>features</c> section, and when the Tools menu opens.
/// The host gates its own side too: <see cref="IInlineEditService.EditAsync"/> fails with <c>PolicyDenied</c> while the
/// flag is off.
/// </summary>
public sealed class InlineChatAvailability : IDisposable
{
    /// <summary>The message shown while inline chat is off.</summary>
    public const string DisabledMessage = "Inline chat is disabled by settings or policy";

    /// <summary>The feature flag's name in <c>effective.features</c>.</summary>
    public const string Flag = "inlineChat";

    private readonly IHostConnection _connection;
    private readonly PillionLog? _log;
    private int _version;
    private volatile bool _isEnabled = true;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="InlineChatAvailability"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public InlineChatAvailability(IHostConnection connection, PillionLog? log = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _log = log;
    }

    /// <summary>Raised, on any thread, when <see cref="IsEnabled"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether inline chat is available. Safe to read on any thread.</summary>
    public bool IsEnabled => _isEnabled;

    /// <summary>Reads the <c>inlineChat</c> flag from the JSON the host returned.</summary>
    /// <param name="json">The <c>effective.features</c> object, or <see langword="null"/> when the host does not know the key.</param>
    /// <returns><see langword="false"/> only when the object says <c>"inlineChat": false</c>.</returns>
    public static bool ReadFlag(string? json) =>
        !(SettingsJsonText.ReadObject(json).TryGetValue(Flag, StringComparison.Ordinal, out var token)
            && token is JValue { Type: JTokenType.Boolean } value
            && !(bool)value);

    /// <summary>Starts following the host.</summary>
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

    /// <summary>Reads the flag from the host. Never throws (except for cancellation); a failure keeps the last answer.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>A task that completes when the answer has been applied.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsUsable(_connection.State))
        {
            return;
        }

        var version = Interlocked.Increment(ref _version);
        bool enabled;
        try
        {
            var json = await _connection.GetProxy<ISettingsService>().GetAsync(ChatAvailability.EffectiveFeaturesKey, cancellationToken).ConfigureAwait(false);
            enabled = ReadFlag(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without an answer the flag stays as it was; the host being away is normal during a restart.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("inlinechat.features.unavailable", "Could not read the effective feature flags.", ("error", ex.GetType().Name));
            return;
        }

        if (version != Volatile.Read(ref _version))
        {
            return; // A newer read is on its way.
        }

        Apply(enabled);
    }

    /// <summary>Sets the flag (tests, and the host's answer).</summary>
    /// <param name="enabled">Whether inline chat is available.</param>
    public void Apply(bool enabled)
    {
        if (_isEnabled == enabled)
        {
            return;
        }

        _isEnabled = enabled;
        _log?.Info("inlinechat.features.changed", enabled ? "Inline chat is enabled." : DisabledMessage + ".");
        Changed?.Invoke(this, EventArgs.Empty);
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

    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (IsUsable(e.State) && !IsUsable(e.PreviousState))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    private void OnHostSettingsChanged(object? sender, HostSettingsChangedEventArgs e)
    {
        if (e.Affects("features"))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }
}
