using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>Where an inline chat session is.</summary>
public enum InlineChatState
{
    /// <summary>The prompt is open and nothing has been asked yet (or the last request was stopped).</summary>
    Prompt,

    /// <summary>A request is streaming; the preview grows as chunks arrive.</summary>
    Streaming,

    /// <summary>A complete proposal is shown: Enter applies it (Insert at cursor or Copy for <c>/test</c>), Esc discards it.</summary>
    Preview,

    /// <summary>The answer to <c>/explain</c> is shown; there is nothing to apply.</summary>
    Explanation,

    /// <summary>The last request failed; Retry sends it again.</summary>
    Failed,

    /// <summary>The session ended: applied, discarded or closed with the document.</summary>
    Closed,
}

/// <summary>Optional dependencies and tuning for <see cref="InlineChatSession"/>.</summary>
public sealed class InlineChatSessionOptions
{
    /// <summary>
    /// Gets or sets how the session reaches the inline edit service. Default: <see cref="InlineChatOverrides.EditService"/>
    /// when set, otherwise the host's proxy from <see cref="IHostConnection.GetProxy{T}"/>, asked again for every request.
    /// </summary>
    public Func<IInlineEditService>? EditService { get; set; }

    /// <summary>Gets or sets the logger. Prompt and code text are never logged.</summary>
    public PillionLog? Log { get; set; }

    /// <summary>Gets or sets how often streamed text is pushed to the UI thread (50 ms by default).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets or sets the id factory for requests and the conversation (a new GUID by default).</summary>
    public Func<string>? NewId { get; set; }

    /// <summary>Gets or sets what is remembered between sessions (recent prompts, model); a private one by default.</summary>
    public InlineChatHistory? History { get; set; }

    /// <summary>Gets or sets the feature flag; <see langword="null"/> means always available.</summary>
    public InlineChatAvailability? Availability { get; set; }

    /// <summary>
    /// Gets or sets how the diagnostics in the document are read for <c>/fix</c> (Roslyn or the Error List), or
    /// <see langword="null"/> when there are none to read.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<InlineEditDiagnostic>>>? Diagnostics { get; set; }

    /// <summary>Gets or sets how long reading the diagnostics may take before the request goes without them (1 s by default).</summary>
    public TimeSpan DiagnosticsTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The view model of one inline chat adornment (design section 5, Inline chat and refactoring; work items 1.6 and 1.7):
/// the one-line prompt with slash-command completion and recent prompts, the model picker, the status line (Thinking,
/// the model, the elapsed time, Stop, the error and Retry), and the proposal shown as a preview until Enter applies it
/// or Esc discards it. A follow-up prompt while a proposal is shown refines it (<see cref="InlineEditRequest.PreviousProposal"/>
/// and the same <see cref="InlineEditRequest.ConversationId"/>). Has no Visual Studio dependencies; the editor side is
/// <see cref="IInlineChatEditor"/>.
/// </summary>
/// <remarks>
/// Threading (design section 13): public members are used on the UI thread. The request runs on a thread-pool thread
/// inside the package's <see cref="JoinableTaskFactory"/>, which switches to the main thread only to update bound
/// properties; streamed text is pushed every <see cref="InlineChatSessionOptions.FlushInterval"/> and raises
/// <see cref="ProposalChanged"/>. Chunks of an older request, and chunks after Stop, Esc or Dispose, are discarded.
/// The document is never touched before <see cref="Accept"/> or <see cref="InsertAtCaret"/>.
/// </remarks>
public sealed class InlineChatSession : ObservableObject, IDisposable
{
    private static readonly TimeSpan CancelCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListModelsTimeout = TimeSpan.FromSeconds(30);

    private readonly IHostConnection _connection;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IInlineChatEditor _editor;
    private readonly Func<IInlineEditService> _editService;
    private readonly PillionLog? _log;
    private readonly TimeSpan _flushInterval;
    private readonly Func<string> _newId;
    private readonly InlineChatHistory _history;
    private readonly InlineChatAvailability? _availability;
    private readonly Func<CancellationToken, Task<IReadOnlyList<InlineEditDiagnostic>>>? _diagnostics;
    private readonly TimeSpan _diagnosticsTimeout;
    private readonly CancellationTokenSource _disposal = new();
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = new();

    private Flight? _flight;
    private ProposalState? _shown;
    private (ParsedPrompt Prompt, string? PreviousProposal)? _lastRequest;
    private int _modelsVersion;
    private bool _updatingModels;
    private bool _suggestionsDismissed;
    private string _promptText = string.Empty;
    private InlineChatState _state = InlineChatState.Prompt;
    private InlineEditCommand _command;
    private string _proposalText = string.Empty;
    private string? _modelHeader;
    private string _elapsedText = string.Empty;
    private string? _errorText;
    private string? _explanation;
    private string? _warning;
    private string? _note;
    private string? _statusOverride;
    private SlashCommandInfo? _selectedSuggestion;
    private bool _isEnabled = true;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="InlineChatSession"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="document">The document and scope the session is about.</param>
    /// <param name="editor">The editor side (apply, insert, copy, close).</param>
    /// <param name="options">Optional dependencies and tuning.</param>
    public InlineChatSession(
        IHostConnection connection,
        JoinableTaskFactory joinableTaskFactory,
        InlineChatDocument document,
        IInlineChatEditor editor,
        InlineChatSessionOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _editService = options?.EditService
            ?? (() => InlineChatOverrides.EditService ?? _connection.GetProxy<IInlineEditService>());
        _log = options?.Log;
        _flushInterval = options?.FlushInterval ?? TimeSpan.FromMilliseconds(50);
        _newId = options?.NewId ?? (() => Guid.NewGuid().ToString("N"));
        _history = options?.History ?? new InlineChatHistory();
        _availability = options?.Availability;
        _diagnostics = options?.Diagnostics;
        _diagnosticsTimeout = options?.DiagnosticsTimeout ?? TimeSpan.FromSeconds(1);
        _isEnabled = _availability?.IsEnabled ?? true;
        ConversationId = _newId();

        SendCommand = new DelegateCommand(() => Run(SendAsync), CanSend);
        StopCommand = new DelegateCommand(() => Run(StopAsync), () => IsBusy);
        RetryCommand = new DelegateCommand(() => Run(RetryAsync), () => CanRetry);
        AcceptCommand = new DelegateCommand(Accept, () => CanAccept);
        DiscardCommand = new DelegateCommand(Discard, () => !_disposed && State != InlineChatState.Closed);
        InsertCommand = new DelegateCommand(InsertAtCaret, () => CanInsert);
        CopyCommand = new DelegateCommand(Copy, () => CanInsert);

        ModelPicker.SelectionChanged += OnModelSelectionChanged;
        foreach (var prompt in _history.Prompts)
        {
            RecentPrompts.Add(prompt);
        }
    }

    /// <summary>Raised on the UI thread when <see cref="ProposalText"/> changed (streamed text, a reset, the final text or a discard).</summary>
    public event EventHandler? ProposalChanged;

    /// <summary>Gets the document and scope.</summary>
    public InlineChatDocument Document { get; }

    /// <summary>Gets the id shared by every prompt of this session.</summary>
    public string ConversationId { get; }

    /// <summary>Gets the model picker (the chat window's option model: Auto, then local, then cloud models).</summary>
    public ModelPickerViewModel ModelPicker { get; } = new();

    /// <summary>Gets the recent prompts, newest first (the last ten of this Visual Studio session).</summary>
    public ObservableCollection<string> RecentPrompts { get; } = [];

    /// <summary>Gets the slash shortcuts that match the prompt (shown while it is <c>/</c> and part of a name).</summary>
    public ObservableCollection<SlashCommandInfo> SlashSuggestions { get; } = [];

    /// <summary>Gets or sets the highlighted slash shortcut.</summary>
    public SlashCommandInfo? SelectedSuggestion
    {
        get => _selectedSuggestion;
        set => SetProperty(ref _selectedSuggestion, value);
    }

    /// <summary>Gets a value indicating whether <see cref="SlashSuggestions"/> has entries.</summary>
    public bool HasSuggestions => SlashSuggestions.Count > 0;

    /// <summary>Gets or sets the prompt text.</summary>
    public string PromptText
    {
        get => _promptText;
        set
        {
            if (SetProperty(ref _promptText, value ?? string.Empty))
            {
                _suggestionsDismissed = false;
                OnPropertyChanged(nameof(HasPromptText));
                UpdateSuggestions();
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets a value indicating whether the prompt has text (hides the placeholder).</summary>
    public bool HasPromptText => PromptText.Length > 0;

    /// <summary>Gets the placeholder shown in the empty prompt.</summary>
    public string Placeholder => State switch
    {
        InlineChatState.Preview when Command == InlineEditCommand.Test => "Type to ask again; Esc closes",
        InlineChatState.Preview => "Type to refine; Enter applies, Esc discards",
        InlineChatState.Explanation => "Type a new instruction; Enter or Esc closes",
        _ => "Ask Pillion to change this code, or type / for /doc, /test, /fix, /explain",
    };

    /// <summary>Gets the state.</summary>
    public InlineChatState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowsDiff));
                OnPropertyChanged(nameof(ShowsExplanation));
                OnPropertyChanged(nameof(IsTestResult));
                OnPropertyChanged(nameof(CanAccept));
                OnPropertyChanged(nameof(CanInsert));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(Placeholder));
                OnPropertyChanged(nameof(StatusText));
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets the slash shortcut of the request shown (or streaming).</summary>
    public InlineEditCommand Command
    {
        get => _command;
        private set
        {
            if (SetProperty(ref _command, value))
            {
                OnPropertyChanged(nameof(ShowsDiff));
                OnPropertyChanged(nameof(ShowsExplanation));
                OnPropertyChanged(nameof(IsTestResult));
                OnPropertyChanged(nameof(CanAccept));
                OnPropertyChanged(nameof(CanInsert));
            }
        }
    }

    /// <summary>Gets a value indicating whether a request is streaming.</summary>
    public bool IsBusy => State == InlineChatState.Streaming;

    /// <summary>Gets a value indicating whether the difference view is shown (streaming or a complete proposal, but not <c>/explain</c>).</summary>
    public bool ShowsDiff => Command != InlineEditCommand.Explain && State is InlineChatState.Streaming or InlineChatState.Preview;

    /// <summary>Gets a value indicating whether the explanation is shown instead of a difference view.</summary>
    public bool ShowsExplanation => State == InlineChatState.Explanation;

    /// <summary>Gets a value indicating whether the shown proposal is new test code (Insert at cursor or Copy, not Replace).</summary>
    public bool IsTestResult => Command == InlineEditCommand.Test && State == InlineChatState.Preview;

    /// <summary>Gets a value indicating whether Enter applies the shown proposal.</summary>
    public bool CanAccept => !_disposed && State == InlineChatState.Preview && Command != InlineEditCommand.Test && _shown is not null;

    /// <summary>Gets a value indicating whether the shown test code can be inserted or copied.</summary>
    public bool CanInsert => !_disposed && IsTestResult && _shown is not null;

    /// <summary>Gets a value indicating whether Retry can send the failed request again.</summary>
    public bool CanRetry => !_disposed && State == InlineChatState.Failed && _lastRequest is not null && IsEnabled;

    /// <summary>Gets a value indicating whether inline chat is enabled (the <c>inlineChat</c> feature flag).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(DisabledMessage));
                OnPropertyChanged(nameof(CanRetry));
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets the message shown while the feature flag is off, or <see langword="null"/>.</summary>
    public string? DisabledMessage => IsEnabled ? null : InlineChatAvailability.DisabledMessage + ".";

    /// <summary>Gets the proposal shown in the difference view (streamed so far, or complete).</summary>
    public string ProposalText
    {
        get => _proposalText;
        private set
        {
            if (SetProperty(ref _proposalText, value ?? string.Empty))
            {
                ProposalChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets the model that answered ("qwen2.5-coder:7b via Ollama, local"), once a chunk names it.</summary>
    public string? ModelHeader
    {
        get => _modelHeader;
        private set
        {
            if (SetProperty(ref _modelHeader, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Gets the time since the request was sent ("2.4 s"), or an empty string.</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>Gets the status line.</summary>
    public string StatusText
    {
        get
        {
            if (_statusOverride is not null)
            {
                return _statusOverride;
            }

            var by = ModelHeader is null ? string.Empty : " " + ModelHeader;
            return State switch
            {
                InlineChatState.Streaming => "Thinking..." + (ModelHeader is null ? string.Empty : " (" + ModelHeader + ")"),
                InlineChatState.Preview when Command == InlineEditCommand.Test => "New test code from" + by + ". Insert at cursor or Copy; Esc closes.",
                InlineChatState.Preview => "Proposal from" + by + ". Enter applies, Esc discards, or type to refine.",
                InlineChatState.Explanation => "Explanation from" + by + ". Esc closes.",
                InlineChatState.Failed => "The request failed.",
                InlineChatState.Closed => string.Empty,
                _ => string.Empty,
            };
        }
    }

    /// <summary>Gets the error of the last request or apply (with the host's retry hint), or <see langword="null"/>.</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="ErrorText"/> is set.</summary>
    public bool HasError => ErrorText is not null;

    /// <summary>Gets the explanation: the answer to <c>/explain</c>, where the test belongs for <c>/test</c>, or the model's note.</summary>
    public string? Explanation
    {
        get => _explanation;
        private set
        {
            if (SetProperty(ref _explanation, value))
            {
                OnPropertyChanged(nameof(HasExplanation));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Explanation"/> is set.</summary>
    public bool HasExplanation => Explanation is not null;

    /// <summary>Gets the warning shown above the difference view (for example that the model proposed no change), or <see langword="null"/>.</summary>
    public string? Warning
    {
        get => _warning;
        private set
        {
            if (SetProperty(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Warning"/> is set.</summary>
    public bool HasWarning => Warning is not null;

    /// <summary>Gets a small note about the proposal (edit blocks the host sent back to the model), or <see langword="null"/>.</summary>
    public string? Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Note"/> is set.</summary>
    public bool HasNote => Note is not null;

    /// <summary>Gets the command behind Enter in the prompt (Send).</summary>
    public DelegateCommand SendCommand { get; }

    /// <summary>Gets the Stop command (asks the host to cancel the request).</summary>
    public DelegateCommand StopCommand { get; }

    /// <summary>Gets the Retry command (sends the failed request again).</summary>
    public DelegateCommand RetryCommand { get; }

    /// <summary>Gets the Apply command (Enter with an empty prompt while a proposal is shown).</summary>
    public DelegateCommand AcceptCommand { get; }

    /// <summary>Gets the Discard command (Esc).</summary>
    public DelegateCommand DiscardCommand { get; }

    /// <summary>Gets the Insert at cursor command (<c>/test</c>).</summary>
    public DelegateCommand InsertCommand { get; }

    /// <summary>Gets the Copy command (<c>/test</c>).</summary>
    public DelegateCommand CopyCommand { get; }

    /// <summary>Gets the time from sending the last request to its first preview line, once known (for the gate's budget).</summary>
    public TimeSpan? FirstLineTime { get; private set; }

    /// <summary>Gets the time from sending the last request to its final chunk, once known.</summary>
    public TimeSpan? TotalTime { get; private set; }

    /// <summary>Starts the session: follows the feature flag and fills the model picker (from memory at once, then from the host).</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        if (_availability is not null)
        {
            _availability.Changed += OnAvailabilityChanged;
        }

        var cached = _history.Models;
        if (cached.Count > 0)
        {
            SetModels(cached);
        }

        Run(RefreshModelsAsync);
    }

    /// <summary>Sends the prompt. While a proposal is shown, an edit prompt refines it.</summary>
    /// <returns>A task that completes when the request has finished.</returns>
    public Task SendAsync()
    {
        if (!CanSend())
        {
            return Task.CompletedTask;
        }

        var prompt = InlineChatPrompt.Parse(PromptText);
        var refine = State == InlineChatState.Preview && _shown is { } shown && IsEdit(shown.Command) && IsEdit(prompt.Command);
        _history.Add(PromptText);
        RecentPrompts.Clear();
        foreach (var recent in _history.Prompts)
        {
            RecentPrompts.Add(recent);
        }

        PromptText = string.Empty;
        return RunRequestAsync(prompt, refine ? _shown!.Text : null);
    }

    /// <summary>Sends the failed request again, with the same prompt and the same proposal being refined.</summary>
    /// <returns>A task that completes when the request has finished.</returns>
    public Task RetryAsync()
    {
        if (!CanRetry || _lastRequest is not { } last)
        {
            return Task.CompletedTask;
        }

        return RunRequestAsync(last.Prompt, last.PreviousProposal);
    }

    /// <summary>Stops the request: asks the host to cancel it, then stops reading. A refined proposal goes back to the previous one.</summary>
    /// <returns>A task that completes when the cancel call has been made.</returns>
    public Task StopAsync()
    {
        Flight? flight;
        lock (_gate)
        {
            flight = _flight;
        }

        if (flight is null || !flight.TryStop())
        {
            return Task.CompletedTask;
        }

        return CancelOnHostAsync(flight);
    }

    /// <summary>Applies the proposal (Enter): replaces the scope as one undoable edit, then closes.</summary>
    public void Accept()
    {
        if (!CanAccept)
        {
            return;
        }

        var reason = _editor.ReplaceScope(Document.ScopeText, _shown!.Text);
        if (reason is null)
        {
            _log?.Info("inlinechat.applied", "Inline edit applied.", ("lines", Document.Scope.LineCount));
            Close();
        }
        else
        {
            ErrorText = reason;
        }
    }

    /// <summary>Inserts the <c>/test</c> code at the caret as one undoable edit, then closes.</summary>
    public void InsertAtCaret()
    {
        if (!CanInsert)
        {
            return;
        }

        var reason = _editor.InsertAtCaret(_shown!.Text);
        if (reason is null)
        {
            Close();
        }
        else
        {
            ErrorText = reason;
        }
    }

    /// <summary>Copies the <c>/test</c> code to the clipboard; the adornment stays open.</summary>
    public void Copy()
    {
        if (!CanInsert)
        {
            return;
        }

        _editor.Copy(_shown!.Text);
        _statusOverride = "Copied the test code to the clipboard. Esc closes.";
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Discards everything (Esc): stops a request in flight and closes. The document is untouched.</summary>
    public void Discard()
    {
        if (_disposed || State == InlineChatState.Closed)
        {
            return;
        }

        Close();
    }

    /// <summary>
    /// Enter in the adornment: accepts the highlighted slash shortcut; sends a typed prompt (or refines the proposal);
    /// with an empty prompt, applies the proposal, or closes an explanation.
    /// </summary>
    public void HandleEnter()
    {
        if (HasSuggestions)
        {
            AcceptSuggestion(SelectedSuggestion ?? SlashSuggestions[0]);
            return;
        }

        if (!string.IsNullOrWhiteSpace(PromptText))
        {
            if (CanSend())
            {
                Run(SendAsync);
            }

            return;
        }

        switch (State)
        {
            case InlineChatState.Preview when CanAccept:
                Accept();
                break;
            case InlineChatState.Explanation:
                Discard();
                break;
        }
    }

    /// <summary>Esc in the adornment: hides the slash-command list if it is open, otherwise discards and closes.</summary>
    public void HandleEscape()
    {
        if (HasSuggestions)
        {
            _suggestionsDismissed = true;
            UpdateSuggestions();
            return;
        }

        Discard();
    }

    /// <summary>Moves the highlight in the slash-command list (Up and Down).</summary>
    /// <param name="delta">-1 for up, 1 for down.</param>
    /// <returns><see langword="true"/> when the list is open and the key was used.</returns>
    public bool MoveSuggestion(int delta)
    {
        if (!HasSuggestions)
        {
            return false;
        }

        var index = SelectedSuggestion is null ? -1 : SlashSuggestions.IndexOf(SelectedSuggestion);
        index = Math.Max(0, Math.Min(SlashSuggestions.Count - 1, index + delta));
        SelectedSuggestion = SlashSuggestions[index];
        return true;
    }

    /// <summary>Puts a slash shortcut in the prompt (Tab or Enter in the list, or a click).</summary>
    /// <param name="suggestion">The shortcut.</param>
    public void AcceptSuggestion(SlashCommandInfo? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        PromptText = suggestion.Name + " ";
        _suggestionsDismissed = true;
        UpdateSuggestions();
    }

    /// <summary>Puts a recent prompt in the prompt box.</summary>
    /// <param name="prompt">The prompt.</param>
    public void UseRecentPrompt(string? prompt)
    {
        if (!string.IsNullOrEmpty(prompt))
        {
            PromptText = prompt!;
        }
    }

    /// <summary>Updates <see cref="ElapsedText"/> (the view calls it on a timer while a request streams).</summary>
    public void Tick()
    {
        if (_elapsed.IsRunning || _elapsed.ElapsedTicks > 0)
        {
            ElapsedText = FormatSeconds(_elapsed.Elapsed);
        }
    }

    /// <summary>Lists the models and reads the privacy settings that grey cloud models out (the inline edit cloud switch).</summary>
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
            _history.Models = options;
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Any failure is shown beside the picker; Auto still works.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Debug("inlinechat.models.failed", "Listing models failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (version != Volatile.Read(ref _modelsVersion))
        {
            return;
        }

        if (options is not null)
        {
            SetModels(options);
        }
        else if (failure!.HostLost)
        {
            ModelPicker.SetWaiting();
        }
        else
        {
            ModelPicker.SetError(failure.Message);
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
        if (_availability is not null)
        {
            _availability.Changed -= OnAvailabilityChanged;
        }

        ModelPicker.SelectionChanged -= OnModelSelectionChanged;
        Flight? flight;
        lock (_gate)
        {
            flight = _flight;
            _flight = null;
        }

        if (flight is not null && flight.TryStop())
        {
            _ = CancelOnHostAsync(flight);
        }

        _elapsed.Stop();

        // Not disposed: background continuations may still read its token.
        _disposal.Cancel();
    }

    /// <summary>Formats a duration for the status line, for example "2.4 s".</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The text.</returns>
    public static string FormatSeconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s";

    private static bool IsEdit(InlineEditCommand command) => command is InlineEditCommand.None or InlineEditCommand.Doc or InlineEditCommand.Fix;

    private bool CanSend() =>
        !_disposed
        && IsEnabled
        && State is not (InlineChatState.Streaming or InlineChatState.Closed)
        && !InlineChatPrompt.Parse(PromptText).IsEmpty;

    private async Task RunRequestAsync(ParsedPrompt prompt, string? previousProposal)
    {
        _lastRequest = (prompt, previousProposal);
        var model = ModelPicker.SelectedModel;
        var flight = new Flight(_newId(), prompt.Command, _shown);
        lock (_gate)
        {
            _flight = flight;
        }

        _statusOverride = null;
        ErrorText = null;
        Warning = null;
        Note = null;
        Explanation = null;
        FirstLineTime = null;
        TotalTime = null;
        ModelHeader = model is { IsAuto: false } ? model.ReplyHeader : null;
        Command = prompt.Command;
        _shown = null;
        ProposalText = string.Empty;
        State = InlineChatState.Streaming;
        _elapsed.Restart();
        Tick();

        RpcFailure? failure = null;
        try
        {
            // Off the UI thread for every RPC await.
            await TaskScheduler.Default;
            var token = flight.Token;
            IReadOnlyList<InlineEditDiagnostic>? diagnostics = null;
            if (prompt.Command == InlineEditCommand.Fix)
            {
                diagnostics = await ReadDiagnosticsAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            var request = InlineEditRequestBuilder.Build(
                Document,
                prompt,
                flight.RequestId,
                ConversationId,
                model is { IsAuto: false } ? model.Id : null, // Auto: no model id, so the host routes by the InlineEdit task class.
                previousProposal,
                diagnostics);
            _log?.Info(
                "inlinechat.request",
                "Inline edit requested.",
                ("request", flight.RequestId),
                ("command", prompt.Command.ToString()),
                ("scope", Document.Scope.Kind.ToString()),
                ("lines", Document.Scope.LineCount),
                ("refine", previousProposal is not null),
                ("diagnostics", request.Diagnostics.Count));

            var service = _editService();
            var accumulator = flight.Accumulator;
            var sinceFlush = Stopwatch.StartNew();
            var flushed = 0;
            string? answeredBy = null;
            await foreach (var chunk in service.EditAsync(request, token).WithCancellation(token).ConfigureAwait(false))
            {
                if (!IsCurrent(flight))
                {
                    break; // Stopped, discarded or replaced: later chunks are stale.
                }

                if (!accumulator.Add(chunk))
                {
                    continue; // Not this request's chunk.
                }

                if (accumulator.HasFirstLine && flight.FirstLine is null)
                {
                    flight.FirstLine = flight.Clock.Elapsed;
                }

                if (accumulator.ModelId is { } modelId)
                {
                    var identity = accumulator.ProviderId + "/" + modelId;
                    if (!string.Equals(identity, answeredBy, StringComparison.Ordinal))
                    {
                        answeredBy = identity;
                        flight.PendingModel = new AnsweredBy(accumulator.ProviderId, modelId);
                    }
                }

                if (accumulator.IsFinal)
                {
                    break;
                }

                if ((accumulator.Version != flushed || flight.PendingModel is not null) && sinceFlush.Elapsed >= _flushInterval)
                {
                    flushed = accumulator.Version;
                    await FlushAsync(flight, accumulator.Text).ConfigureAwait(false);
                    sinceFlush.Restart();
                }
            }

            if (IsCurrent(flight) && !accumulator.IsFinal)
            {
                failure = RpcFailure.FromError(new PillionError
                {
                    Code = PillionErrorCode.ProviderUnavailable,
                    Message = "Pillion Host ended the proposal before it was complete.",
                    RetryHint = "Retry the request.",
                });
            }
            else if (accumulator.Error is { } error)
            {
                failure = RpcFailure.FromError(error);
            }
        }
#pragma warning disable CA1031 // Every failure ends the request with a message and Retry.
        catch (Exception) when (flight.IsStopped)
        {
            // Stopped or discarded: the cancellation is expected.
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning(
                "inlinechat.failed",
                "An inline edit failed.",
                ("request", flight.RequestId),
                ("error", ex.GetType().Name),
                ("code", failure.Code?.ToString()));
        }

        await FinishAsync(flight, failure).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InlineEditDiagnostic>?> ReadDiagnosticsAsync(CancellationToken token)
    {
        if (_diagnostics is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_diagnosticsTimeout);
            return await _diagnostics(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _log?.Debug("inlinechat.diagnostics.timeout", "Reading the diagnostics took too long; sending without them.");
            return null;
        }
#pragma warning disable CA1031 // The request goes without diagnostics rather than failing.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log?.Debug("inlinechat.diagnostics.failed", "Reading the diagnostics failed.", ("error", ex.GetType().Name));
            return null;
        }
    }

    private async Task FlushAsync(Flight flight, string text)
    {
        var answeredBy = flight.TakePendingModel();
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (IsCurrent(flight))
        {
            if (answeredBy is not null)
            {
                ModelHeader = HeaderFor(answeredBy);
            }

            ProposalText = text;
            Tick();
        }

        await TaskScheduler.Default;
    }

    private async Task FinishAsync(Flight flight, RpcFailure? failure)
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

        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_flight, flight);
            if (current)
            {
                _flight = null;
            }
        }

        flight.Dispose();
        if (!current || _disposed || State == InlineChatState.Closed)
        {
            return;
        }

        _elapsed.Stop();
        Tick();
        var accumulator = flight.Accumulator;
        if (flight.TakePendingModel() is { } answeredBy)
        {
            ModelHeader = HeaderFor(answeredBy);
        }

        if (flight.IsStopped)
        {
            // Back to what was shown before this request: the previous proposal when refining, else the empty prompt.
            Restore(flight.Previous);
            _statusOverride = flight.Previous is null ? "Stopped." : "Stopped; the previous proposal is shown.";
            OnPropertyChanged(nameof(StatusText));
            _log?.Info("inlinechat.stopped", "Inline edit stopped.", ("request", flight.RequestId), ("ms", (long)flight.Clock.Elapsed.TotalMilliseconds));
            return;
        }

        if (failure is not null)
        {
            // Any preview is discarded: only a validated final proposal can be applied.
            ProposalText = string.Empty;
            ErrorText = failure.RetryHint is null ? failure.Message : failure.Message + " " + failure.RetryHint;
            State = InlineChatState.Failed;
            return;
        }

        FirstLineTime = flight.FirstLine ?? flight.Clock.Elapsed;
        TotalTime = flight.Clock.Elapsed;
        _log?.Info(
            "inlinechat.timing",
            "Inline edit complete.",
            ("request", flight.RequestId),
            ("command", flight.Command.ToString()),
            ("firstLineMs", (long)FirstLineTime.Value.TotalMilliseconds),
            ("totalMs", (long)TotalTime.Value.TotalMilliseconds),
            ("resets", accumulator.Resets),
            ("rejectedBlocks", accumulator.RejectedBlocks),
            ("model", accumulator.ProviderId + "/" + accumulator.ModelId));

        Explanation = accumulator.Explanation;
        Warning = accumulator.Warning;
        Note = accumulator.RejectedBlocks > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Pillion sent {0} edit block{1} back to the model before this proposal matched the code.",
                accumulator.RejectedBlocks,
                accumulator.RejectedBlocks == 1 ? string.Empty : "s")
            : null;
        if (flight.Command == InlineEditCommand.Explain)
        {
            ProposalText = string.Empty;
            Explanation ??= "The model gave no explanation.";
            State = InlineChatState.Explanation;
        }
        else
        {
            _shown = new ProposalState(accumulator.Text, flight.Command, ModelHeader, Explanation, Warning, Note);
            ProposalText = accumulator.Text;
            State = InlineChatState.Preview;
        }

        OnPropertyChanged(nameof(CanAccept));
        OnPropertyChanged(nameof(CanInsert));
        UpdateCommands();
    }

    private void Restore(ProposalState? previous)
    {
        _shown = previous;
        if (previous is null)
        {
            Command = InlineEditCommand.None;
            ProposalText = string.Empty;
            State = InlineChatState.Prompt;
        }
        else
        {
            Command = previous.Command;
            ModelHeader = previous.ModelHeader;
            Explanation = previous.Explanation;
            Warning = previous.Warning;
            Note = previous.Note;
            ProposalText = previous.Text;
            State = InlineChatState.Preview;
        }

        OnPropertyChanged(nameof(CanAccept));
        OnPropertyChanged(nameof(CanInsert));
        UpdateCommands();
    }

    private void Close()
    {
        Flight? flight;
        lock (_gate)
        {
            flight = _flight;
            _flight = null;
        }

        if (flight is not null && flight.TryStop())
        {
            _ = CancelOnHostAsync(flight);
        }

        _elapsed.Stop();
        State = InlineChatState.Closed;
        _editor.Close();
    }

    private Task CancelOnHostAsync(Flight flight) => _joinableTaskFactory.RunAsync(async () =>
    {
        await TaskScheduler.Default;
        Task? call = null;
        try
        {
            using var timeout = new CancellationTokenSource(CancelCallTimeout);
            call = _editService().CancelEditAsync(flight.RequestId, timeout.Token);
        }
#pragma warning disable CA1031 // Best effort: the local cancellation below stops the stream anyway.
        catch (Exception ex)
        {
            _log?.Debug("inlinechat.cancel.failed", "Asking the host to cancel failed.", ("request", flight.RequestId), ("error", ex.GetType().Name));
        }

        // Stop reading once the cancel call is on its way.
        flight.Cancel();
        if (call is not null)
        {
            try
            {
                await call.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Debug("inlinechat.cancel.failed", "Asking the host to cancel failed.", ("request", flight.RequestId), ("error", ex.GetType().Name));
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
            var cloud = SettingsJsonText.ReadBool(await settings.GetAsync(SettingsJsonText.CloudSwitchesKey + ".inlineEdit", cancellationToken).ConfigureAwait(false), fallback: true);
            var providers = SettingsJsonText.ReadProviderSummaries(await settings.GetAsync(SettingsJsonText.ProvidersKey, cancellationToken).ConfigureAwait(false));
            return new ChatModelPolicy(localOnly, cloud, providers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without settings the picker still works; models are shown as the host listed them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("inlinechat.settings.failed", "Reading the privacy settings for the model picker failed.", ("error", ex.GetType().Name));
            return ChatModelPolicy.Default;
        }
    }

    /// <summary>Fills the picker and picks the model chosen last time (Auto by default). Call on the UI thread.</summary>
    private void SetModels(IReadOnlyList<ModelOption> options)
    {
        var preferred = _history.LastModelId ?? ModelOption.AutoId;
        _updatingModels = true;
        try
        {
            ModelPicker.SetModels(options);
            if (ModelPicker.Models.FirstOrDefault(m => m.IsAvailable && string.Equals(m.Id, preferred, StringComparison.Ordinal)) is { } match)
            {
                ModelPicker.SelectedModel = match;
            }
        }
        finally
        {
            _updatingModels = false;
        }
    }

    private void OnModelSelectionChanged(object? sender, EventArgs e)
    {
        if (!_updatingModels && ModelPicker.SelectedModel is { } model)
        {
            _history.LastModelId = model.Id;
        }
    }

    private void UpdateSuggestions()
    {
        IReadOnlyList<SlashCommandInfo> matches = _suggestionsDismissed ? [] : InlineChatPrompt.Suggest(PromptText);
        if (matches.SequenceEqual(SlashSuggestions))
        {
            return;
        }

        SlashSuggestions.Clear();
        foreach (var match in matches)
        {
            SlashSuggestions.Add(match);
        }

        SelectedSuggestion = SlashSuggestions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void OnAvailabilityChanged(object? sender, EventArgs e) => Run(async () =>
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        IsEnabled = _availability?.IsEnabled ?? true;
    });

    /// <summary>Names the model a chunk reported, from the picker's entries (read on the UI thread, so a list that arrived meanwhile counts).</summary>
    private string HeaderFor(AnsweredBy answeredBy) => ModelOption.ReplyHeaderFor(ModelPicker.Models, answeredBy.ProviderId, answeredBy.ModelId);

    private bool IsCurrent(Flight flight)
    {
        lock (_gate)
        {
            return ReferenceEquals(_flight, flight) && !flight.IsStopped;
        }
    }

    private void UpdateCommands()
    {
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        AcceptCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        InsertCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
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
                // The adornment is closing.
            }
#pragma warning disable CA1031 // Fire-and-forget UI work must not crash Visual Studio; failures are logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("inlinechat.unexpected", "Unexpected inline chat failure.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        });
    }

    /// <summary>The provider and model a chunk named.</summary>
    private sealed class AnsweredBy(string? providerId, string modelId)
    {
        public string? ProviderId { get; } = providerId;

        public string ModelId { get; } = modelId;
    }

    /// <summary>A complete proposal as it was shown, so a stopped refinement can go back to it.</summary>
    private sealed class ProposalState(string text, InlineEditCommand command, string? modelHeader, string? explanation, string? warning, string? note)
    {
        public string Text { get; } = text;

        public InlineEditCommand Command { get; } = command;

        public string? ModelHeader { get; } = modelHeader;

        public string? Explanation { get; } = explanation;

        public string? Warning { get; } = warning;

        public string? Note { get; } = note;
    }

    /// <summary>A request in flight: its id, its cancellation, the chunks so far and what was shown before it.</summary>
    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private AnsweredBy? _pendingModel;
        private int _stopped;
        private int _disposed;

        public Flight(string requestId, InlineEditCommand command, ProposalState? previous)
        {
            RequestId = requestId;
            Command = command;
            Previous = previous;
            Token = _cancellation.Token;
            Accumulator = new InlineProposalAccumulator(requestId);
        }

        public string RequestId { get; }

        public InlineEditCommand Command { get; }

        public ProposalState? Previous { get; }

        public CancellationToken Token { get; }

        public InlineProposalAccumulator Accumulator { get; }

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public TimeSpan? FirstLine { get; set; }

        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        /// <summary>Gets or sets the model a chunk named, not yet shown (set on the stream's thread, taken by the flush).</summary>
        public AnsweredBy? PendingModel
        {
            get => Volatile.Read(ref _pendingModel);
            set => Volatile.Write(ref _pendingModel, value);
        }

        public AnsweredBy? TakePendingModel() => Interlocked.Exchange(ref _pendingModel, null);

        /// <summary>Marks the request stopped; returns <see langword="false"/> when it already was.</summary>
        public bool TryStop() => Interlocked.Exchange(ref _stopped, 1) == 0;

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request ended meanwhile.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }
    }
}
