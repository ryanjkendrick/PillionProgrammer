using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Whether chat is available, according to the host's effective feature flags (the <c>chat</c> flag after the user
/// settings and repository policies are applied; read through <c>ISettingsService.GetAsync("effective.features")</c>),
/// and whether the chat window's mentions (<c>mentions</c>) and Context Inspector and Outbound log (<c>outboundLogView</c>)
/// are, and whether the Index window, its status bar indicator and the Indexing Options page (<c>indexWindow</c>, work item
/// 2.15) are.
/// Chat is available until the host says otherwise: before the first answer, when the host is not connected, when
/// the host is older than the flags, and when the answer cannot be read. The last answer is kept while the host
/// restarts and refreshed when it is ready again.
/// </summary>
public sealed class ChatAvailability : IDisposable
{
    /// <summary>The message shown in the chat window (and for the disabled command) while chat is off.</summary>
    public const string DisabledMessage = "Chat is disabled by settings or policy";

    /// <summary>The virtual settings key that holds the effective feature flags.</summary>
    public const string EffectiveFeaturesKey = "effective.features";

    private const string ChatFlag = "chat";
    private const string MentionsFlag = "mentions";
    private const string OutboundLogViewFlag = "outboundLogView";
    private const string IndexWindowFlag = "indexWindow";

    private readonly IHostConnection _connection;
    private readonly PillionLog? _log;
    private int _version;
    private volatile bool _isEnabled = true;
    private volatile bool _isMentionsEnabled = true;
    private volatile bool _isOutboundLogEnabled = true;
    private volatile bool _isIndexWindowEnabled = true;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ChatAvailability"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public ChatAvailability(IHostConnection connection, PillionLog? log = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _log = log;
    }

    /// <summary>Raised, on any thread, when <see cref="IsEnabled"/>, <see cref="IsMentionsEnabled"/>, <see cref="IsOutboundLogEnabled"/> or <see cref="IsIndexWindowEnabled"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether chat is available. Safe to read on any thread.</summary>
    public bool IsEnabled => _isEnabled;

    /// <summary>Gets a value indicating whether the chat window offers <c>@</c>-mentions and pins (the <c>mentions</c> flag). Safe to read on any thread.</summary>
    public bool IsMentionsEnabled => _isMentionsEnabled;

    /// <summary>Gets a value indicating whether the chat window shows the Context Inspector and the Outbound log (the <c>outboundLogView</c> flag). Safe to read on any thread.</summary>
    public bool IsOutboundLogEnabled => _isOutboundLogEnabled;

    /// <summary>Gets a value indicating whether the Index window, its status bar indicator and the Indexing Options page are available (the <c>indexWindow</c> flag). Safe to read on any thread.</summary>
    public bool IsIndexWindowEnabled => _isIndexWindowEnabled;

    /// <summary>Starts following the host: the flags are read every time the host becomes ready.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _connection.StateChanged += OnHostStateChanged;
        if (IsUsable(_connection.State))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    /// <summary>Reads the effective feature flags from the host. Never throws (except for cancellation); a failure keeps the last answer.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>A task that completes when the answer has been applied.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _version);
        bool enabled;
        bool mentions;
        bool outboundLog;
        bool indexWindow;
        try
        {
            var json = await _connection.GetProxy<ISettingsService>().GetAsync(EffectiveFeaturesKey, cancellationToken).ConfigureAwait(false);
            enabled = ReadChatFlag(json);
            mentions = ReadFlag(json, MentionsFlag);
            outboundLog = ReadFlag(json, OutboundLogViewFlag);
            indexWindow = ReadFlag(json, IndexWindowFlag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without an answer chat stays as it was; the host being away is normal during a restart.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("chat.features.unavailable", "Could not read the effective feature flags.", ("error", ex.GetType().Name));
            return;
        }

        if (version != Volatile.Read(ref _version))
        {
            return; // A newer read is on its way.
        }

        Apply(enabled, mentions, outboundLog, indexWindow);
    }

    /// <summary>Reads the <c>chat</c> flag from the JSON the host returned.</summary>
    /// <param name="json">The JSON object, or <see langword="null"/> when the host does not know the key.</param>
    /// <returns><see langword="false"/> only when the object says <c>"chat": false</c>.</returns>
    public static bool ReadChatFlag(string? json) => ReadFlag(json, ChatFlag);

    /// <summary>Reads a feature flag from the JSON the host returned; a flag the host does not report is on.</summary>
    /// <param name="json">The JSON object, or <see langword="null"/> when the host does not know the key.</param>
    /// <param name="flag">The flag's name, for example <c>mentions</c>.</param>
    /// <returns><see langword="false"/> only when the object says <c>false</c> for the flag.</returns>
    public static bool ReadFlag(string? json, string flag) =>
        !(SettingsJsonText.ReadObject(json).TryGetValue(flag, StringComparison.Ordinal, out var token)
            && token is JValue { Type: JTokenType.Boolean } value
            && !(bool)value);

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
        }
    }

    private static bool IsUsable(HostState state) => state is HostState.Ready or HostState.Degraded;

    private void Apply(bool enabled, bool mentions, bool outboundLog, bool indexWindow)
    {
        if (_isEnabled == enabled && _isMentionsEnabled == mentions && _isOutboundLogEnabled == outboundLog && _isIndexWindowEnabled == indexWindow)
        {
            return;
        }

        if (_isEnabled != enabled)
        {
            _log?.Info("chat.features.changed", enabled ? "Chat is enabled." : "Chat is disabled by settings or policy.");
        }

        _isEnabled = enabled;
        _isMentionsEnabled = mentions;
        _isOutboundLogEnabled = outboundLog;
        _isIndexWindowEnabled = indexWindow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (IsUsable(e.State) && !IsUsable(e.PreviousState))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }
}
