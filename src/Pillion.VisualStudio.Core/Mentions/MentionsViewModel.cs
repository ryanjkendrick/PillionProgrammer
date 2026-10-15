using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>A key the mention picker may handle while it is open.</summary>
public enum MentionKey
{
    /// <summary>Down arrow: the next entry.</summary>
    Down,

    /// <summary>Up arrow: the previous entry.</summary>
    Up,

    /// <summary>Enter: pick the highlighted entry.</summary>
    Enter,

    /// <summary>Tab: pick the highlighted entry.</summary>
    Tab,

    /// <summary>Esc: close the picker.</summary>
    Escape,
}

/// <summary>A change to the chat input the mentions ask for: replace a span and put the caret somewhere.</summary>
public sealed class MentionInputEditEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="MentionInputEditEventArgs"/> class.</summary>
    /// <param name="start">Where the replaced span starts.</param>
    /// <param name="length">The length of the replaced span.</param>
    /// <param name="replacement">The new text.</param>
    /// <param name="caret">Where the caret goes afterwards.</param>
    public MentionInputEditEventArgs(int start, int length, string replacement, int caret)
    {
        Start = start;
        Length = length;
        Replacement = replacement;
        Caret = caret;
    }

    /// <summary>Gets where the replaced span starts.</summary>
    public int Start { get; }

    /// <summary>Gets the length of the replaced span.</summary>
    public int Length { get; }

    /// <summary>Gets the new text.</summary>
    public string Replacement { get; }

    /// <summary>Gets where the caret goes afterwards.</summary>
    public int Caret { get; }

    /// <summary>Applies the edit to a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The edited text.</returns>
    public string Apply(string text)
    {
        text ??= string.Empty;
        var start = Math.Max(0, Math.Min(Start, text.Length));
        var length = Math.Max(0, Math.Min(Length, text.Length - start));
        return text.Substring(0, start) + Replacement + text.Substring(start + length);
    }
}

/// <summary>A file that needs the user's approval before it is pinned (it is excluded or looks like it holds secrets).</summary>
public sealed class MentionApproval
{
    /// <summary>Initializes a new instance of the <see cref="MentionApproval"/> class.</summary>
    /// <param name="request">The request to send again with <see cref="MentionRequest.ApprovedSensitive"/>.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="path">The file's path, when the host said.</param>
    /// <param name="conversation">The conversation the pin goes to.</param>
    internal MentionApproval(MentionRequest request, string name, string? path, ChatConversation conversation)
    {
        Request = request;
        Name = name;
        Path = path;
        Conversation = conversation;
    }

    /// <summary>Gets the file's name.</summary>
    public string Name { get; }

    /// <summary>Gets the file's path, when known.</summary>
    public string? Path { get; }

    /// <summary>Gets the question shown ("This file looks like it holds secrets. Pin it anyway?").</summary>
    public string Prompt => ChatStrings.Format(ChatStrings.PinApprovalPromptFormat, Name);

    internal MentionRequest Request { get; }

    internal ChatConversation Conversation { get; }
}

/// <summary>
/// The chat window's <c>@</c>-mentions (work item 1.9; design section 7, Context pinning): the picker at the caret, the
/// resolution of a picked entry through <see cref="IContextService.ResolveMentionAsync"/>, the sub-picker for an ambiguous
/// match, the per-file approval for an excluded or secret file, the pins of the current conversation and the reason shown
/// when a mention does not resolve. Call public members on the UI thread; RPC calls run on the thread pool.
/// </summary>
public sealed class MentionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly IHostConnection _connection;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IWorkspaceFiles? _files;
    private readonly IChatEditorContext? _editor;
    private readonly PillionLog? _log;
    private readonly CancellationTokenSource _disposal = new();
    private ChatConversation _conversation;
    private MentionApproval? _pendingApproval;
    private string _notice = string.Empty;
    private int _resolving;
    private bool _isEnabled = true;
    private string _lastText = string.Empty;
    private int _lastCaret;
    private int _filesRequested;
    private bool _disposed;
    private CancellationTokenSource? _suggestCancellation;
    private JoinableTask? _suggestions;

    /// <summary>Initializes a new instance of the <see cref="MentionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="conversation">The conversation whose pins are shown first.</param>
    /// <param name="files">The solution or folder file list; without it only exact paths resolve.</param>
    /// <param name="editor">The editor's selection, clipboard and diagnostics; without it those mentions have no payload.</param>
    /// <param name="log">The logger; mention text and content are never logged.</param>
    public MentionsViewModel(
        IHostConnection connection,
        JoinableTaskFactory joinableTaskFactory,
        ChatConversation conversation,
        IWorkspaceFiles? files = null,
        IChatEditorContext? editor = null,
        PillionLog? log = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _files = files;
        _editor = editor;
        _log = log;
        RemovePinCommand = new DelegateCommand(p => RemovePin(p as MentionPin), p => p is MentionPin);
        ApproveCommand = new DelegateCommand(() => Run(ApproveAsync), () => PendingApproval is not null);
        DeclineCommand = new DelegateCommand(Decline, () => PendingApproval is not null);
        DismissNoticeCommand = new DelegateCommand(() => Notice = string.Empty, () => HasNotice);
        if (_files is not null)
        {
            _files.Changed += OnFilesChanged;
        }
    }

    /// <summary>Raised on the UI thread when a picked entry changes the input (the typed mention is replaced or removed).</summary>
    public event EventHandler<MentionInputEditEventArgs>? InputEditRequested;

    /// <summary>Gets the picker.</summary>
    public MentionPickerViewModel Picker { get; } = new();

    /// <summary>Gets the pins of the current conversation, in the order they were pinned.</summary>
    public ObservableCollection<MentionPin> Pins => _conversation.Pins;

    /// <summary>Gets or sets a value indicating whether mentions are available (the <c>mentions</c> feature flag). When off the picker never opens.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value) && !value)
            {
                Picker.Close();
                PendingApproval = null;
                Notice = string.Empty;
            }
        }
    }

    /// <summary>Gets the file waiting for the user's approval, or <see langword="null"/>.</summary>
    public MentionApproval? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (SetProperty(ref _pendingApproval, value))
            {
                OnPropertyChanged(nameof(HasPendingApproval));
                ApproveCommand.RaiseCanExecuteChanged();
                DeclineCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets a value indicating whether a file waits for approval.</summary>
    public bool HasPendingApproval => _pendingApproval is not null;

    /// <summary>Gets the line shown under the pins when a mention did not resolve (its reason), or empty.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasNotice));
                DismissNoticeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Notice"/> has text.</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <summary>Gets a value indicating whether a mention is being resolved.</summary>
    public bool IsResolving => Volatile.Read(ref _resolving) > 0;

    /// <summary>Gets the command that removes a pin; its parameter is the <see cref="MentionPin"/>.</summary>
    public DelegateCommand RemovePinCommand { get; }

    /// <summary>Gets the command that pins the file waiting for approval.</summary>
    public DelegateCommand ApproveCommand { get; }

    /// <summary>Gets the command that leaves the file waiting for approval unpinned.</summary>
    public DelegateCommand DeclineCommand { get; }

    /// <summary>Gets the command that hides <see cref="Notice"/>.</summary>
    public DelegateCommand DismissNoticeCommand { get; }

    /// <summary>Gets or sets how long typing must pause before the host is asked for symbols or documents (tests set zero).</summary>
    internal TimeSpan SuggestionDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Builds the request a mention sends: its kind, the kind typed in <c>@kind:value</c> as the qualifier, and the options of <c>@git</c> and <c>@output</c> as arguments.</summary>
    /// <param name="mention">The mention text.</param>
    /// <param name="kind">The kind the client chose, or empty to read it from the text.</param>
    /// <returns>The request (without the roots, files and payload, which <c>CompleteRequestAsync</c> adds).</returns>
    internal static MentionRequest MakeRequest(string mention, string kind)
    {
        var parsed = MentionSyntax.Parse(mention);
        var effective = string.IsNullOrEmpty(kind) ? parsed.Kind : kind;
        return new MentionRequest
        {
            Mention = mention,
            Kind = effective,
            Qualifier = parsed.HasKindSeparator ? parsed.Kind : string.Empty,
            Arguments = MentionSyntax.ArgumentsOf(parsed),
        };
    }

    /// <summary>A task that completes when the host suggestions asked for so far have arrived (for tests).</summary>
    /// <returns>The task.</returns>
    internal Task WhenSuggestionsDoneAsync() => _suggestions?.JoinAsync() ?? Task.CompletedTask;

    /// <summary>Shows another conversation's pins. Call on the UI thread.</summary>
    /// <param name="conversation">The conversation.</param>
    public void SetConversation(ChatConversation conversation)
    {
        if (conversation is null || ReferenceEquals(conversation, _conversation))
        {
            return;
        }

        _conversation = conversation;
        Picker.Close();
        PendingApproval = null;
        Notice = string.Empty;
        OnPropertyChanged(nameof(Pins));
    }

    /// <summary>
    /// Puts back the pins a saved conversation had (work item 2.2). Each shows at once as a stale chip with its saved name and
    /// token badge; file and folder pins are then resolved again, off the UI thread, and the ones that still resolve replace
    /// their chip with a fresh pin. A pin whose file moved, that needs approval, or that holds client-only text (selection,
    /// clipboard, errors) stays stale: nothing is sent for it. Saving is paused while this runs. Call on the UI thread.
    /// </summary>
    /// <param name="conversation">The conversation the pins belong to.</param>
    /// <param name="saved">What the host stored.</param>
    /// <returns>A task that completes when the pins that could be read again have been refreshed.</returns>
    public async Task RestorePinsAsync(ChatConversation conversation, IReadOnlyList<ConversationPin> saved)
    {
        if (conversation is null || saved is null || _disposed)
        {
            return;
        }

        conversation.IsRestoringPins = true;
        var placeholders = new List<(MentionPin Pin, ConversationPin Saved)>();
        try
        {
            conversation.Pins.Clear();
            foreach (var pin in saved.Where(p => p is not null))
            {
                var placeholder = MentionPin.FromSaved(pin);
                placeholders.Add((placeholder, pin));
                conversation.Pins.Add(placeholder);
            }

            // Files, folders, symbols and documents are read again by name; the repo map and git are read again as they are
            // now. Client-only text (selection, clipboard, errors, the Output panes, the last build and tests) stays stale.
            var rereadable = placeholders.Where(p => IsEnabled
                && ((p.Saved.Kind is MentionKinds.File or MentionKinds.Folder or MentionKinds.Symbol or MentionKinds.Docs && !string.IsNullOrEmpty(p.Saved.Target))
                    || p.Saved.Kind is MentionKinds.Solution or MentionKinds.Git)).ToList();
            if (rereadable.Count == 0)
            {
                return;
            }

            var fresh = new List<(MentionPin Placeholder, MentionResolution Result)>();
            await TaskScheduler.Default;
            foreach (var (placeholder, pin) in rereadable)
            {
                try
                {
                    var request = MakeRequest("@" + pin.Kind + (string.IsNullOrEmpty(pin.Target) ? string.Empty : ":" + pin.Target), pin.Kind);
                    await CompleteRequestAsync(request, _disposal.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
                    timeout.CancelAfter(ResolveTimeout);
                    var result = await _connection.GetProxy<IContextService>().ResolveMentionAsync(request, timeout.Token).ConfigureAwait(false);
                    if (result is { IsResolved: true })
                    {
                        fresh.Add((placeholder, result));
                    }
                }
                catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A pin that cannot be read again stays stale.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log?.Debug("chat.pins.restore.failed", "Reading a restored pin again failed.", ("kind", pin.Kind), ("error", ex.GetType().Name));
                }
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            foreach (var (placeholder, result) in fresh)
            {
                var at = conversation.Pins.IndexOf(placeholder);
                if (at >= 0)
                {
                    conversation.Pins[at] = new MentionPin(result);
                }
            }
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            // The window is closing.
        }
        finally
        {
            conversation.IsRestoringPins = false;
        }
    }

    /// <summary>Follows the input: opens, filters or closes the picker. Call on every text or caret change.</summary>
    /// <param name="text">The input text.</param>
    /// <param name="caret">The caret index.</param>
    public void UpdateInput(string? text, int caret)
    {
        _lastText = text ?? string.Empty;
        _lastCaret = caret;
        if (!IsEnabled || _disposed)
        {
            return;
        }

        var wasOpen = Picker.IsOpen;
        var files = _files?.Current ?? WorkspaceFileList.Empty;
        Picker.Update(_lastText, caret, files);
        ScheduleSuggestions();
        if (Picker.IsOpen && !wasOpen && _files is not null && Interlocked.Exchange(ref _filesRequested, 1) == 0)
        {
            // The picker opened: collect the file list if it is missing or stale (the cached list is shown meanwhile);
            // Changed re-filters the open picker when a new list arrives.
            Run(async () =>
            {
                try
                {
                    await TaskScheduler.Default;
                    await _files.GetAsync(_disposal.Token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _filesRequested, 0);
                }
            });
        }
    }

    /// <summary>Handles a key while the picker is open.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the picker used the key (the input must not).</returns>
    public bool HandleKey(MentionKey key)
    {
        if (!Picker.IsOpen)
        {
            return false;
        }

        switch (key)
        {
            case MentionKey.Down:
                Picker.Move(1);
                return true;
            case MentionKey.Up:
                Picker.Move(-1);
                return true;
            case MentionKey.Escape:
                Picker.Dismiss();
                return true;
            default:
                if (Picker.SelectedItem is not { } item)
                {
                    // Nothing to pick: Esc-like for Tab, and Enter falls through to Send.
                    Picker.Dismiss();
                    return key == MentionKey.Tab;
                }

                Run(() => CommitAsync(item));
                return true;
        }
    }

    /// <summary>
    /// Picks an entry: a kind that takes a value (<c>@file</c>, <c>@folder</c>) becomes <c>@kind:</c> in the input and the
    /// picker lists files or folders; any other entry is removed from the input and resolved into a pin.
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <returns>A task that completes when the entry has been resolved (or at once for <c>@file</c> and <c>@folder</c>).</returns>
    public Task CommitAsync(MentionPickerItem item)
    {
        if (item is null || !IsEnabled || _disposed)
        {
            return Task.CompletedTask;
        }

        var token = Picker.Token;
        if (item.ItemKind == MentionPickerItemKind.Kind && MentionSyntax.OpensSubPicker(item.MentionKind) && token is not null)
        {
            var replacement = "@" + item.MentionKind + ":";
            var edit = new MentionInputEditEventArgs(token.Start, token.Length, replacement, token.Start + replacement.Length);
            RequestEdit(edit);
            return Task.CompletedTask;
        }

        Picker.Close();
        if (token is not null)
        {
            // Remove the typed mention (and the space after it) from the message: it becomes a pin.
            var length = token.Length;
            if (token.Start + length < _lastText.Length && _lastText[token.Start + length] == ' ')
            {
                length++;
            }

            RequestEdit(new MentionInputEditEventArgs(token.Start, length, string.Empty, token.Start));
        }

        CancelSuggestions();
        var request = MakeRequest(item.MentionText, item.MentionKind);
        if (item.Prefetched is { } prefetched)
        {
            // The host resolved this entry while it was typed: pin that answer.
            Notice = string.Empty;
            Apply(request, prefetched, _conversation);
            return Task.CompletedTask;
        }

        return ResolveAsync(request, _conversation);
    }

    /// <summary>Resolves a mention typed in full (for example <c>@file:src/Program.cs</c>) into a pin of the current conversation.</summary>
    /// <param name="mention">The mention text.</param>
    /// <returns>A task that completes when it has been resolved.</returns>
    public Task ResolveTextAsync(string mention)
    {
        return ResolveAsync(MakeRequest(mention, string.Empty), _conversation);
    }

    /// <summary>Removes a pin from the current conversation. Call on the UI thread.</summary>
    /// <param name="pin">The pin.</param>
    public void RemovePin(MentionPin? pin)
    {
        if (pin is not null)
        {
            _conversation.Pins.Remove(pin);
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
        if (_files is not null)
        {
            _files.Changed -= OnFilesChanged;
        }

        CancelSuggestions();
        _disposal.Cancel();
    }

    private void RequestEdit(MentionInputEditEventArgs edit)
    {
        _lastText = edit.Apply(_lastText);
        _lastCaret = edit.Caret;
        InputEditRequested?.Invoke(this, edit);
    }

    private async Task ResolveAsync(MentionRequest request, ChatConversation conversation)
    {
        Interlocked.Increment(ref _resolving);
        OnPropertyChanged(nameof(IsResolving));
        Notice = string.Empty;
        MentionResolution? result = null;
        RpcFailure? failure = null;
        try
        {
            await TaskScheduler.Default;
            await CompleteRequestAsync(request, _disposal.Token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ResolveTimeout);
            result = await _connection.GetProxy<IContextService>().ResolveMentionAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Any failure is shown under the input.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.mention.failed", "Resolving a mention failed.", ("kind", request.Kind), ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }
        finally
        {
            Interlocked.Decrement(ref _resolving);
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        OnPropertyChanged(nameof(IsResolving));
        if (failure is not null || result is null)
        {
            Notice = failure?.Message ?? ChatStrings.MentionFailed;
            return;
        }

        Apply(request, result, conversation);
    }

    /// <summary>Fills in what only the client knows: the roots and file list, and the payload of the editor's kinds.</summary>
    private async Task CompleteRequestAsync(MentionRequest request, CancellationToken cancellationToken)
    {
        var kind = string.IsNullOrEmpty(request.Kind) ? MentionSyntax.Parse(request.Mention).Kind : request.Kind;
        var files = _files is null ? WorkspaceFileList.Empty : await _files.GetAsync(cancellationToken).ConfigureAwait(false);
        request.WorkspaceRoots = [.. files.Roots];
        if (kind is "" or MentionSyntax.File or MentionSyntax.Folder)
        {
            request.CandidateFiles = [.. files.Files];
        }

        if (_editor is null)
        {
            return;
        }

        request.Payload = kind switch
        {
            MentionSyntax.Selection => await _editor.GetSelectionAsync(cancellationToken).ConfigureAwait(false),
            MentionSyntax.Clipboard => await _editor.GetClipboardTextAsync(cancellationToken).ConfigureAwait(false),
            MentionSyntax.Errors => await _editor.GetErrorsPayloadAsync(cancellationToken).ConfigureAwait(false),
            MentionSyntax.Output => await _editor.GetOutputPaneTextAsync(OutputPaneOf(request), cancellationToken).ConfigureAwait(false),
            MentionSyntax.Build => await _editor.GetLastBuildAsync(cancellationToken).ConfigureAwait(false),
            MentionSyntax.Tests => await _editor.GetTestResultsAsync(cancellationToken).ConfigureAwait(false),
            _ => request.Payload,
        };
    }

    /// <summary>The Output pane an <c>@output</c> request names (Build when it names none).</summary>
    private static string OutputPaneOf(MentionRequest request)
    {
        var pane = request.Arguments?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim() ?? string.Empty;
        return pane.ToLowerInvariant() switch
        {
            "" or "build" => "Build",
            "debug" => "Debug",
            "test" or "tests" => "Tests",
            _ => pane,
        };
    }

    /// <summary>Asks the host for symbols or documents matching the value typed after <c>@symbol:</c> or <c>@docs:</c>, once typing pauses.</summary>
    private void ScheduleSuggestions()
    {
        CancelSuggestions();
        if (!Picker.WantsHostItems || Picker.Token is not { } token || _disposed)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
        _suggestCancellation = cancellation;
        var delay = SuggestionDelay;
        _suggestions = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await SuggestAsync(token, delay, cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Typing moved on, or the window is closing.
            }
#pragma warning disable CA1031 // Suggestions are optional; a failure only leaves the picker as it is.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Debug("chat.mention.suggest.failed", "Asking the host for mention matches failed.", ("kind", token.Parsed.Kind), ("error", ex.GetType().Name));
            }
        });
    }

    private void CancelSuggestions()
    {
        var pending = _suggestCancellation;
        _suggestCancellation = null;
        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
        }
    }

    private async Task SuggestAsync(MentionToken token, TimeSpan delay, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var kind = token.Parsed.Kind;
        var request = MakeRequest("@" + kind + ":" + token.Parsed.Value, kind);
        await CompleteRequestAsync(request, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        var result = await _connection.GetProxy<IContextService>().ResolveMentionAsync(request, timeout.Token).ConfigureAwait(false);
        var roots = request.WorkspaceRoots ?? [];
        var items = new List<MentionPickerItem>();
        if (result is { IsResolved: true })
        {
            items.Add(kind == MentionSyntax.Symbol
                ? MentionPickerViewModel.SymbolItem(MentionPickerItemKind.Symbol, SymbolValueOf(result), roots, result)
                : MentionPickerViewModel.DocItem(result.FilePath ?? result.DisplayName, roots, result));
        }
        else if (result is { Candidates.Length: > 0 })
        {
            items.AddRange(result.Candidates.Select(c => kind == MentionSyntax.Symbol
                ? MentionPickerViewModel.SymbolItem(MentionPickerItemKind.Symbol, c, roots)
                : MentionPickerViewModel.DocItem(c, roots)));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (!_disposed)
        {
            Picker.SetHostItems(token, items);
        }
    }

    /// <summary>The <c>Name#path:line</c> value of a resolved symbol, from its name, file and first citation.</summary>
    private static string SymbolValueOf(MentionResolution result)
    {
        var line = result.Citations is { Length: > 0 } citations ? citations[0].StartLine + 1 : 0;
        return string.IsNullOrEmpty(result.FilePath)
            ? result.DisplayName
            : result.DisplayName + "#" + result.FilePath + (line > 0 ? ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
    }

    private void Apply(MentionRequest request, MentionResolution result, ChatConversation conversation)
    {
        if (result.IsResolved)
        {
            var existing = conversation.Pins.FirstOrDefault(p => p.IsSameAs(result));
            var pin = new MentionPin(result);
            if (existing is not null)
            {
                // Pinning the same thing again refreshes its content in place (the pin order stays, so the prompt prefix does too).
                conversation.Pins[conversation.Pins.IndexOf(existing)] = pin;
            }
            else
            {
                conversation.Pins.Add(pin);
            }

            return;
        }

        if (result.RequiresApproval)
        {
            var name = !string.IsNullOrWhiteSpace(result.DisplayName) ? result.DisplayName
                : !string.IsNullOrEmpty(result.FilePath) ? System.IO.Path.GetFileName(result.FilePath)
                : result.Mention;
            PendingApproval = new MentionApproval(request, name, result.FilePath, conversation);
            return;
        }

        if (result.Candidates is { Length: > 0 } candidates)
        {
            Picker.ShowCandidates(result.Kind, result.Mention, candidates, _files?.Current.Roots ?? []);
            return;
        }

        Notice = string.IsNullOrWhiteSpace(result.Reason)
            ? ChatStrings.Format(ChatStrings.MentionUnresolvedFormat, result.Mention)
            : result.Mention + ": " + result.Reason;
    }

    private Task ApproveAsync()
    {
        var approval = PendingApproval;
        PendingApproval = null;
        if (approval is null)
        {
            return Task.CompletedTask;
        }

        var original = approval.Request;
        var request = new MentionRequest
        {
            Mention = original.Mention,
            Kind = original.Kind,
            WorkspaceRoots = original.WorkspaceRoots,
            CandidateFiles = original.CandidateFiles,
            Payload = original.Payload,
            MaxTokens = original.MaxTokens,
            ApprovedSensitive = true,
            Qualifier = original.Qualifier,
            Arguments = original.Arguments,
        };
        return ResolveAsync(request, approval.Conversation);
    }

    private void Decline() => PendingApproval = null;

    private void OnFilesChanged(object? sender, EventArgs e) =>
        Run(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            if (!_disposed && Picker.IsOpen && !Picker.IsCandidates)
            {
                Picker.Update(_lastText, _lastCaret, _files!.Current);
            }
        });

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
                _log?.Error("chat.mention.unexpected", "Unexpected mention failure.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        });
    }
}
