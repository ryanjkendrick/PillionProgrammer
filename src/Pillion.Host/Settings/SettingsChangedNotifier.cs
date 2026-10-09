using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Host.Connectivity;

namespace Pillion.Host.Settings;

/// <summary>Options for <see cref="SettingsChangedNotifier"/>.</summary>
internal sealed class SettingsChangedNotifierOptions
{
    /// <summary>Gets or sets how long changes are coalesced into one notification. The default is 250 ms.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Tells the connected extension which top-level sections of <c>settings.json</c> changed, so pickers and Options
/// pages refresh without a button. Sends the notification <see cref="NotificationName"/> after any
/// <see cref="SettingsStore.Changed"/> (a save through <c>ISettingsService</c> or an external edit), once per burst of
/// changes (debounced), and only to a client whose handshake was accepted: with no client connected it sends nothing,
/// and a client that connects later reads the settings itself.
/// </summary>
internal sealed partial class SettingsChangedNotifier : IHostedService, IDisposable
{
    /// <summary>
    /// The notification sent to the client. One positional argument: a string array of the names of the top-level
    /// sections that changed (for example <c>["routing", "cloudSwitches"]</c>), in document order.
    /// </summary>
    public const string NotificationName = "settingsChanged";

    private readonly SettingsStore _store;
    private readonly INotificationSender _sender;
    private readonly TimeProvider _time;
    private readonly SettingsChangedNotifierOptions _options;
    private readonly ILogger<SettingsChangedNotifier> _logger;
    private readonly Lock _gate = new();

    private JsonObject? _snapshot;
    private ITimer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SettingsChangedNotifier"/> class.</summary>
    /// <param name="store">The settings store.</param>
    /// <param name="sender">Sends notifications to the client.</param>
    /// <param name="time">The clock used for the debounce.</param>
    /// <param name="options">The notifier options.</param>
    /// <param name="logger">The logger.</param>
    public SettingsChangedNotifier(
        SettingsStore store,
        INotificationSender sender,
        TimeProvider time,
        SettingsChangedNotifierOptions options,
        ILogger<SettingsChangedNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _sender = sender;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Finds the top-level sections whose content differs. A section that is only in one of the documents counts as
    /// changed. Sections are listed in the order of <paramref name="after"/>, then those only in <paramref name="before"/>.
    /// </summary>
    /// <param name="before">The earlier document, or <see langword="null"/> when there is none (every section counts as changed).</param>
    /// <param name="after">The later document.</param>
    /// <returns>The names of the changed sections.</returns>
    public static IReadOnlyList<string> ChangedSections(JsonObject? before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(after);

        var changed = new List<string>();
        foreach (var (name, value) in after)
        {
            if (before is null || !before.TryGetPropertyValue(name, out var previous) || !JsonNode.DeepEquals(previous, value))
            {
                changed.Add(name);
            }
        }

        if (before is not null)
        {
            foreach (var (name, _) in before)
            {
                if (!after.ContainsKey(name))
                {
                    changed.Add(name);
                }
            }
        }

        return changed;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _timer = _time.CreateTimer(OnTimer, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _store.Changed += OnChanged;
            _snapshot = TryReadDocument();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                _store.Changed -= OnChanged;
            }

            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnChanged(object? sender, SettingsChangedEventArgs e)
    {
        lock (_gate)
        {
            // Every change restarts the window, so a burst of saves (an Options page writes several keys) is one notification.
            _timer?.Change(_options.DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

#pragma warning disable CA1031 // A timer callback must never let an exception escape (it would end the process).
    private void OnTimer(object? state)
    {
        IReadOnlyList<string> changed;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var current = _store.GetDocument();
                changed = ChangedSections(_snapshot, current);
                _snapshot = current;
            }
        }
        catch (Exception ex)
        {
            LogReadFailed(_logger, ex);
            return;
        }

        if (changed.Count == 0)
        {
            return;
        }

        _ = SendAsync(changed);
    }

    private async Task SendAsync(IReadOnlyList<string> changed)
    {
        try
        {
            if (await _sender.TrySendAsync(NotificationName, changed.ToArray()).ConfigureAwait(false))
            {
                LogSent(_logger, changed);
            }
        }
        catch (Exception ex)
        {
            LogSendFailed(_logger, ex);
        }
    }

    private JsonObject? TryReadDocument()
    {
        try
        {
            return _store.GetDocument();
        }
        catch (Exception ex)
        {
            // An invalid file is reported where the settings are used; the first change then counts every section.
            LogReadFailed(_logger, ex);
            return null;
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 310, Level = LogLevel.Debug, Message = "Sent settingsChanged([{Sections}]) to the client.")]
    private static partial void LogSent(ILogger logger, IReadOnlyList<string> sections);

    [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "Could not read the settings to work out what changed; no settingsChanged notification was sent.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 312, Level = LogLevel.Warning, Message = "Sending settingsChanged failed.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);
}

/// <summary>Dependency injection registration for the <c>settingsChanged</c> notification.</summary>
internal static class SettingsChangedServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hosted service that sends <c>settingsChanged</c>. Requires the RPC server (<c>AddPillionRpcServer</c>);
    /// settings services are added with <c>AddPillionSettings</c> when missing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPillionSettingsNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPillionSettings();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SettingsChangedNotifierOptions>();
        services.TryAddSingleton<INotificationSender, VsixNotificationSender>();
        services.AddHostedService<SettingsChangedNotifier>();
        return services;
    }
}
