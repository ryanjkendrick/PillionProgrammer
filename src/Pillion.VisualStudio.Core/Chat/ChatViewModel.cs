using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Optional dependencies and tuning for <see cref="ChatViewModel"/>.</summary>
public sealed class ChatViewModelOptions
{
    /// <summary>Gets or sets the action behind the status line's Open Logs link.</summary>
    public Action? OpenLogs { get; set; }

    /// <summary>Gets or sets the logger. Message text is never logged.</summary>
    public PillionLog? Log { get; set; }

    /// <summary>Gets or sets how often streamed text is pushed to the UI thread (50 ms by default).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets or sets the request id factory (a new GUID by default).</summary>
    public Func<string>? NewRequestId { get; set; }
}

/// <summary>
/// The Pillion Chat tool window's view model: model picker, transcript, input, Send and Stop, Clear, Retry and the host
/// status line. Driven by <see cref="IHostConnection"/>; has no Visual Studio dependencies.
/// </summary>
/// <remarks>
/// Threading (design doc section 13): public members and commands are used on the UI thread. Every RPC call is awaited
/// on a thread-pool thread inside the package's <see cref="JoinableTaskFactory"/>, which switches back to the main
/// thread only to update bound properties: streamed text is pushed in batches every
/// <see cref="ChatViewModelOptions.FlushInterval"/>. Each reply has its own request id; chunks for any other id, and
/// chunks that arrive after Stop, Clear or a host restart, are discarded.
/// </remarks>
public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CancelCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListModelsTimeout = TimeSpan.FromSeconds(30);

    private readonly IHostConnection _connection;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action? _openLogs;
    private readonly PillionLog? _log;
    private readonly TimeSpan _flushInterval;
    private readonly Func<string> _newRequestId;
    private readonly CancellationTokenSource _disposal = new();
    private readonly object _gate = new();

    private InFlight? _inFlight;
    private int _stateVersion;
    private int _modelsVersion;
    private string _inputText = string.Empty;
    private bool _isSending;
    private HostState _hostState;
    private ConnectivityState? _connectivity;
    private int _connectivityVersion;
    private (int RestartAttempt, int MaxRestartAttempts, string? Reason) _lastChange;
    private string _statusText;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ChatViewModel"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="options">Optional dependencies and tuning.</param>
    public ChatViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, ChatViewModelOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _openLogs = options?.OpenLogs;
        _log = options?.Log;
        _flushInterval = options?.FlushInterval ?? TimeSpan.FromMilliseconds(50);
        _newRequestId = options?.NewRequestId ?? (() => Guid.NewGuid().ToString("N"));
        _hostState = connection.State;
        _connectivity = connection.Connectivity;
        _statusText = FormatStatus(_hostState, 0, 0, reason: null, _connectivity);

        SendCommand = new DelegateCommand(() => Run(SendAsync), CanSend);
        StopCommand = new DelegateCommand(() => Run(StopAsync), () => IsSending);
        ClearCommand = new DelegateCommand(Clear, () => Turns.Count > 0 || IsSending);
        RetryCommand = new DelegateCommand(p => Run(() => RetryAsync(p as ChatTurnViewModel)), p => CanRetry(p as ChatTurnViewModel));
        RefreshModelsCommand = new DelegateCommand(() => Run(RefreshModelsAsync), () => IsHostUsable);
        OpenLogsCommand = new DelegateCommand(() => _openLogs?.Invoke(), () => _openLogs is not null);

        ModelPicker.SelectionChanged += (_, _) => UpdateCommands();
        Turns.CollectionChanged += (_, _) => ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Gets the model picker.</summary>
    public ModelPickerViewModel ModelPicker { get; } = new();

    /// <summary>Gets the transcript, oldest first.</summary>
    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];

    /// <summary>Gets or sets the message being typed.</summary>
    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets a value indicating whether a reply is streaming.</summary>
    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets the host state shown on the status line.</summary>
    public HostState HostState
    {
        get => _hostState;
        private set
        {
            if (SetProperty(ref _hostState, value))
            {
                OnPropertyChanged(nameof(IsHostUsable));
                OnPropertyChanged(nameof(IsHostFailed));
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets the host's connectivity shown on the status line, or <see langword="null"/> when it has not reported one.</summary>
    public ConnectivityState? Connectivity
    {
        get => _connectivity;
        private set => SetProperty(ref _connectivity, value);
    }

    /// <summary>Gets a value indicating whether the host can serve requests (Ready or Degraded).</summary>
    public bool IsHostUsable => IsUsable(HostState);

    /// <summary>Gets a value indicating whether the host has failed (the status line then offers Open Logs).</summary>
    public bool IsHostFailed => HostState == HostState.Failed;

    /// <summary>Gets the status line text.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Gets the Send command (Enter in the input box).</summary>
    public DelegateCommand SendCommand { get; }

    /// <summary>Gets the Stop command: cancels the streaming reply on the host and locally.</summary>
    public DelegateCommand StopCommand { get; }

    /// <summary>Gets the Clear conversation command.</summary>
    public DelegateCommand ClearCommand { get; }

    /// <summary>Gets the Retry command; its parameter is the failed or interrupted reply.</summary>
    public DelegateCommand RetryCommand { get; }

    /// <summary>Gets the command that lists the models again (and re-reads the privacy settings).</summary>
    public DelegateCommand RefreshModelsCommand { get; }

    /// <summary>Gets the Open Logs command.</summary>
    public DelegateCommand OpenLogsCommand { get; }

    /// <summary>
    /// Starts following the host state and, once the host is ready, lists the models. Call once, on the UI thread.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _connection.StateChanged += OnHostStateChanged;
        _connection.ConnectivityChanged += OnConnectivityChanged;
        Connectivity = _connection.Connectivity;
        ApplyHostState(_connection.State, 0, 0, reason: null);
        if (IsHostUsable)
        {
            Run(RefreshModelsAsync);
        }
    }

    /// <summary>Sends <see cref="InputText"/> and streams the reply. Call on the UI thread.</summary>
    /// <returns>A task that completes when the reply has finished, stopped or failed.</returns>
    public Task SendAsync()
    {
        if (!CanSend())
        {
            return Task.CompletedTask;
        }

        var model = ModelPicker.SelectedModel!;
        var text = InputText.Trim();
        Turns.Add(ChatTurnViewModel.ForUser(text));
        InputText = string.Empty;
        return StreamReplyAsync(model);
    }

    /// <summary>
    /// Stops the streaming reply: calls <see cref="IModelService.CancelAsync"/> with its request id and cancels the
    /// stream. The text received so far is kept. Call on the UI thread.
    /// </summary>
    /// <returns>A task that completes when the host has been asked to cancel.</returns>
    public Task StopAsync()
    {
        InFlight? flight;
        lock (_gate)
        {
            flight = _inFlight;
        }

        return flight is not null && flight.TryEnd(ChatTurnState.Stopped, cancel: false)
            ? CancelOnHostAsync(flight)
            : Task.CompletedTask;
    }

    /// <summary>Clears the conversation, stopping any streaming reply. Call on the UI thread.</summary>
    public void Clear()
    {
        InFlight? flight;
        lock (_gate)
        {
            flight = _inFlight;
            _inFlight = null;
        }

        if (flight is not null && flight.TryEnd(ChatTurnState.Stopped, cancel: false))
        {
            Run(() => CancelOnHostAsync(flight));
        }

        Turns.Clear();
        IsSending = false;
        UpdateCommands();
    }

    /// <summary>Retries a failed or interrupted reply (the last turn). Call on the UI thread.</summary>
    /// <param name="turn">The reply to retry, or <see langword="null"/> for the last turn.</param>
    /// <returns>A task that completes when the new reply has finished.</returns>
    public Task RetryAsync(ChatTurnViewModel? turn)
    {
        turn ??= Turns.LastOrDefault();
        if (!CanRetry(turn))
        {
            return Task.CompletedTask;
        }

        var model = ModelPicker.Models.FirstOrDefault(m => m.IsAvailable && string.Equals(m.Id, turn!.ModelId, StringComparison.Ordinal))
            ?? ModelPicker.SelectedModel;
        if (model is not { IsAvailable: true })
        {
            return Task.CompletedTask;
        }

        Turns.Remove(turn!);
        return StreamReplyAsync(model);
    }

    /// <summary>Lists the models and reads the privacy settings that grey cloud models out.</summary>
    /// <returns>A task that completes when the picker has been updated.</returns>
    public async Task RefreshModelsAsync()
    {
        var version = Interlocked.Increment(ref _modelsVersion);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        ModelPicker.BeginLoading();

        await TaskScheduler.Default;
        IReadOnlyList<ModelOption>? options = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ListModelsTimeout);
            var models = await _connection.GetProxy<IModelService>().ListModelsAsync(timeout.Token).ConfigureAwait(false);
            var policy = await ReadPolicyAsync(timeout.Token).ConfigureAwait(false);
            options = ModelOption.Build(models, policy);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Any failure is shown beside the picker.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.models.failed", "Listing models failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (version != Volatile.Read(ref _modelsVersion))
        {
            return;
        }

        if (options is not null)
        {
            ModelPicker.SetModels(options);
        }
        else if (failure!.HostLost)
        {
            ModelPicker.SetWaiting();
        }
        else
        {
            ModelPicker.SetError(failure.Message);
        }

        UpdateCommands();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.StateChanged -= OnHostStateChanged;
        _connection.ConnectivityChanged -= OnConnectivityChanged;
        InFlight? flight;
        lock (_gate)
        {
            flight = _inFlight;
            _inFlight = null;
        }

        flight?.TryEnd(ChatTurnState.Stopped, cancel: true);

        // Not disposed: background continuations may still read its token.
        _disposal.Cancel();
    }

    /// <summary>Formats the status line.</summary>
    /// <param name="state">The host state.</param>
    /// <param name="restartAttempt">The restart attempt, or 0.</param>
    /// <param name="maxRestartAttempts">The crash limit, or 0.</param>
    /// <param name="reason">Why the host failed, when known.</param>
    /// <returns>The text.</returns>
    public static string FormatStatus(HostState state, int restartAttempt, int maxRestartAttempts, string? reason) => state switch
    {
        HostState.Starting => "Starting Pillion Host...",
        HostState.Ready => "Ready.",
        HostState.Degraded => "Ready; some providers are unavailable.",
        HostState.Restarting when restartAttempt > 0 && maxRestartAttempts > 0 => string.Format(
            CultureInfo.CurrentCulture, "Pillion Host is restarting ({0}/{1})...", restartAttempt, maxRestartAttempts),
        HostState.Restarting => "Pillion Host is restarting...",
        HostState.Failed => string.IsNullOrWhiteSpace(reason) ? "Pillion Host failed." : "Pillion Host failed: " + reason,
        _ => state.ToString(),
    };

    /// <summary>Formats the status line with the host's connectivity, which is shown while the host is usable.</summary>
    /// <param name="state">The host state.</param>
    /// <param name="restartAttempt">The restart attempt, or 0.</param>
    /// <param name="maxRestartAttempts">The crash limit, or 0.</param>
    /// <param name="reason">Why the host failed, when known.</param>
    /// <param name="connectivity">The host's connectivity, or <see langword="null"/> when unknown.</param>
    /// <returns>The text, for example "Ready. Offline: cloud providers cannot be reached; local models still work."</returns>
    public static string FormatStatus(HostState state, int restartAttempt, int maxRestartAttempts, string? reason, ConnectivityState? connectivity)
    {
        var text = FormatStatus(state, restartAttempt, maxRestartAttempts, reason);
        if (connectivity is not { } value || !IsUsable(state))
        {
            return text;
        }

        return text + " " + value switch
        {
            ConnectivityState.Online => "Online.",
            ConnectivityState.Degraded => "Degraded: some cloud providers cannot be reached.",
            ConnectivityState.Offline => "Offline: cloud providers cannot be reached; local models still work.",
            ConnectivityState.LocalOnly => "Local-only: nothing leaves this machine.",
            _ => HostStatusText.ConnectivityName(value) + ".",
        };
    }

    private static bool IsUsable(HostState state) => state is HostState.Ready or HostState.Degraded;

    private bool CanSend() =>
        !_disposed
        && IsHostUsable
        && !IsSending
        && !string.IsNullOrWhiteSpace(InputText)
        && ModelPicker.SelectedModel is { IsAvailable: true };

    private bool CanRetry(ChatTurnViewModel? turn) =>
        !_disposed && turn is { CanRetry: true } && IsHostUsable && !IsSending && Turns.LastOrDefault() == turn;

    private async Task StreamReplyAsync(ModelOption model)
    {
        var messages = BuildHistory();
        var requestId = _newRequestId();
        var listed = ModelPicker.Models.ToList(); // On the UI thread: names the model a chunk reports.
        var reply = ChatTurnViewModel.ForAssistant(model);
        reply.RequestId = requestId;
        reply.RetryCommand = RetryCommand;
        var flight = new InFlight(requestId, reply);
        lock (_gate)
        {
            _inFlight = flight;
        }

        Turns.Add(reply);
        IsSending = true;
        UpdateRetry();

        var pending = new StringBuilder();
        var outcome = ChatTurnState.Complete;
        RpcFailure? failure = null;
        try
        {
            // Off the UI thread for every RPC await.
            await TaskScheduler.Default;
            var token = flight.Token;
            token.ThrowIfCancellationRequested();
            var request = new ChatRequest
            {
                RequestId = requestId,
                Messages = messages,
                ModelId = model.IsAuto ? null : model.Id, // Auto: no model id, so the host's router decides.
                Profile = Profile.Auto,
            };

            var service = _connection.GetProxy<IModelService>();
            var sinceFlush = Stopwatch.StartNew();
            string? answeredBy = null;
            await foreach (var chunk in service.ChatAsync(request, token).WithCancellation(token).ConfigureAwait(false))
            {
                if (chunk is null || (!string.IsNullOrEmpty(chunk.RequestId) && !string.Equals(chunk.RequestId, requestId, StringComparison.Ordinal)))
                {
                    continue; // Not this request's chunk.
                }

                if (!IsCurrent(flight))
                {
                    break; // Stopped, cleared or interrupted: later chunks are stale.
                }

                // The header names the model the host says answered (it can differ from the pick after a fallback).
                if (!string.IsNullOrEmpty(chunk.ModelId))
                {
                    var identity = chunk.ProviderId + "/" + chunk.ModelId;
                    if (!string.Equals(identity, answeredBy, StringComparison.Ordinal))
                    {
                        answeredBy = identity;
                        flight.PendingHeader = ModelOption.ReplyHeaderFor(listed, chunk.ProviderId, chunk.ModelId);
                    }
                }

                pending.Append(chunk.Text);
                if (chunk.IsFinal)
                {
                    if (chunk.Error is { } error)
                    {
                        // The stream failed after some text: keep the text and show the error under it.
                        failure = RpcFailure.FromError(error);
                        outcome = failure.HostLost ? ChatTurnState.Interrupted : ChatTurnState.Failed;
                        _log?.Warning("chat.reply.failed", "A chat reply failed part way.", ("request", requestId), ("code", error.Code.ToString()));
                    }

                    break;
                }

                if ((pending.Length > 0 || flight.PendingHeader is not null) && sinceFlush.Elapsed >= _flushInterval)
                {
                    await FlushAsync(flight, pending).ConfigureAwait(false);
                    sinceFlush.Restart();
                }
            }
        }
#pragma warning disable CA1031 // Every failure ends the reply with an inline message.
        catch (Exception) when (flight.EndState is not null)
        {
            // Stopped, cleared or interrupted: the cancellation or lost connection is expected.
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            outcome = ChatTurnState.Stopped;
        }
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            outcome = failure.HostLost ? ChatTurnState.Interrupted : ChatTurnState.Failed;
            _log?.Warning(
                "chat.reply.failed",
                "A chat reply failed.",
                ("request", requestId),
                ("error", ex.GetType().Name),
                ("code", failure.Code?.ToString()));
        }

        var final = flight.TryEnd(outcome, cancel: outcome != ChatTurnState.Complete) ? outcome : flight.EndState!.Value;
        await FinishAsync(flight, pending, final, final == ChatTurnState.Failed ? failure : null).ConfigureAwait(false);
    }

    private async Task FlushAsync(InFlight flight, StringBuilder pending)
    {
        var text = pending.ToString();
        pending.Clear();
        var header = flight.TakePendingHeader();
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (header is not null)
        {
            flight.Reply.SetHeader(header);
        }

        flight.Reply.Append(text);
        await TaskScheduler.Default;
    }

    private async Task FinishAsync(InFlight flight, StringBuilder pending, ChatTurnState state, RpcFailure? failure)
    {
        try
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        }
        catch (OperationCanceledException)
        {
            flight.Dispose();
            return;
        }

        if (flight.TakePendingHeader() is { } header)
        {
            flight.Reply.SetHeader(header);
        }

        if (pending.Length > 0)
        {
            flight.Reply.Append(pending.ToString());
        }

        flight.Reply.Finish(state, failure?.Message, failure?.RetryHint);
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_inFlight, flight);
            if (current)
            {
                _inFlight = null;
            }
        }

        flight.Dispose();
        if (current)
        {
            IsSending = false;
        }

        UpdateRetry();
        UpdateCommands();
    }

    private Task CancelOnHostAsync(InFlight flight) => _joinableTaskFactory.RunAsync(async () =>
    {
        await TaskScheduler.Default;
        Task? call = null;
        try
        {
            using var timeout = new CancellationTokenSource(CancelCallTimeout);
            call = _connection.GetProxy<IModelService>().CancelAsync(flight.RequestId, timeout.Token);
        }
#pragma warning disable CA1031 // Best effort: the local cancellation below stops the stream anyway.
        catch (Exception ex)
        {
            _log?.Debug("chat.cancel.failed", "Asking the host to cancel failed.", ("request", flight.RequestId), ("error", ex.GetType().Name));
        }

        // Cancel the stream once the cancel call is on its way.
        flight.Cancel();
        if (call is not null)
        {
            try
            {
                await call.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Debug("chat.cancel.failed", "Asking the host to cancel failed.", ("request", flight.RequestId), ("error", ex.GetType().Name));
            }
        }
#pragma warning restore CA1031
    }).Task;

    private async Task<ChatModelPolicy> ReadPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _connection.GetProxy<ISettingsService>();
            var localOnly = SettingsJsonText.ReadBool(await settings.GetAsync(SettingsJsonText.LocalOnlyKey, cancellationToken).ConfigureAwait(false), fallback: false);
            var cloudChat = SettingsJsonText.ReadBool(await settings.GetAsync(SettingsJsonText.CloudSwitchesKey + ".chat", cancellationToken).ConfigureAwait(false), fallback: true);
            var providers = SettingsJsonText.ReadProviderSummaries(await settings.GetAsync(SettingsJsonText.ProvidersKey, cancellationToken).ConfigureAwait(false));
            return new ChatModelPolicy(localOnly, cloudChat, providers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without settings the picker still works; models are shown as the host listed them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("chat.settings.failed", "Reading the privacy settings for the model picker failed.", ("error", ex.GetType().Name));
            return ChatModelPolicy.Default;
        }
    }

    private List<ChatMessage> BuildHistory()
    {
        var messages = new List<ChatMessage>();
        foreach (var turn in Turns)
        {
            if (turn.IsUser)
            {
                messages.Add(new ChatMessage { Role = ChatRole.User, Content = turn.Text });
            }
            else if (turn.State is ChatTurnState.Complete or ChatTurnState.Stopped && turn.Text.Length > 0)
            {
                messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = turn.Text });
            }
        }

        return messages;
    }

    private bool IsCurrent(InFlight flight)
    {
        if (flight.EndState is not null)
        {
            return false;
        }

        lock (_gate)
        {
            return ReferenceEquals(_inFlight, flight);
        }
    }

    /// <summary>Raised on a background thread.</summary>
    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _stateVersion);
        if (!IsUsable(e.State))
        {
            InFlight? flight;
            lock (_gate)
            {
                flight = _inFlight;
            }

            flight?.TryEnd(ChatTurnState.Interrupted, cancel: true);
        }

        Run(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            if (version != Volatile.Read(ref _stateVersion) || _disposed)
            {
                return; // A newer change is on its way.
            }

            var wasUsable = IsHostUsable;
            ApplyHostState(e.State, e.RestartAttempt, e.MaxRestartAttempts, e.Reason);
            if (IsHostUsable && (!wasUsable || !IsUsable(e.PreviousState)))
            {
                await RefreshModelsAsync().ConfigureAwait(true);
            }
            else if (!IsHostUsable)
            {
                ModelPicker.SetWaiting();
            }
        });
    }

    private void ApplyHostState(HostState state, int restartAttempt, int maxRestartAttempts, string? reason)
    {
        HostState = state;
        _lastChange = (restartAttempt, maxRestartAttempts, reason);
        StatusText = FormatStatus(state, restartAttempt, maxRestartAttempts, reason, Connectivity);
    }

    /// <summary>Raised on a background thread. Updates the status line and lists the models again (availability follows connectivity).</summary>
    private void OnConnectivityChanged(object? sender, HostConnectivityChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _connectivityVersion);
        Run(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            if (version != Volatile.Read(ref _connectivityVersion) || _disposed)
            {
                return; // A newer change is on its way.
            }

            var current = _connection.Connectivity;
            if (current == Connectivity)
            {
                return;
            }

            var previous = Connectivity;
            Connectivity = current;
            StatusText = FormatStatus(HostState, _lastChange.RestartAttempt, _lastChange.MaxRestartAttempts, _lastChange.Reason, current);

            // The first report after a (re)connect arrives with the model list already loading; later changes can
            // grey models out or bring them back.
            if (IsHostUsable && previous is not null && current is not null)
            {
                await RefreshModelsAsync().ConfigureAwait(true);
            }
        });
    }

    private void UpdateRetry()
    {
        var last = Turns.LastOrDefault();
        foreach (var turn in Turns)
        {
            turn.CanRetry = !IsSending
                && ReferenceEquals(turn, last)
                && turn.Role == ChatTurnRole.Assistant
                && turn.State is ChatTurnState.Failed or ChatTurnState.Interrupted;
        }
    }

    private void UpdateCommands()
    {
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        RefreshModelsCommand.RaiseCanExecuteChanged();
    }

    private void Run(Func<Task> work)
    {
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
            {
                // The window is closing.
            }
#pragma warning disable CA1031 // Fire-and-forget UI work must not crash Visual Studio; failures are logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("chat.unexpected", "Unexpected chat window failure.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        });
    }

    /// <summary>A reply in flight: its request id, its turn, its cancellation and how it ended.</summary>
    private sealed class InFlight : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private int _endState = -1;
        private string? _pendingHeader;
        private bool _disposed;

        public InFlight(string requestId, ChatTurnViewModel reply)
        {
            RequestId = requestId;
            Reply = reply;
            Token = _cancellation.Token;
        }

        public string RequestId { get; }

        public ChatTurnViewModel Reply { get; }

        public CancellationToken Token { get; }

        /// <summary>Gets or sets a reply header not yet shown (set on the stream's thread, taken by the flush).</summary>
        public string? PendingHeader
        {
            get => Volatile.Read(ref _pendingHeader);
            set => Volatile.Write(ref _pendingHeader, value);
        }

        public ChatTurnState? EndState
        {
            get
            {
                var value = Volatile.Read(ref _endState);
                return value < 0 ? null : (ChatTurnState)value;
            }
        }

        public string? TakePendingHeader() => Interlocked.Exchange(ref _pendingHeader, null);

        /// <summary>Records how the reply ended; the first caller wins.</summary>
        public bool TryEnd(ChatTurnState state, bool cancel)
        {
            if (Interlocked.CompareExchange(ref _endState, (int)state, -1) != -1)
            {
                return false;
            }

            if (cancel)
            {
                Cancel();
            }

            return true;
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
