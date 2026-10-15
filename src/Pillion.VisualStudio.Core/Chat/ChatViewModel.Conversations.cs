using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Raised by the Privacy options page after Delete all, so the open Chat window forgets the conversations it listed.</summary>
public sealed class ConversationEvents
{
    /// <summary>Raised when every saved conversation was deleted.</summary>
    public event EventHandler? AllDeleted;

    /// <summary>Raises <see cref="AllDeleted"/>. Call on the UI thread.</summary>
    public void RaiseAllDeleted() => AllDeleted?.Invoke(this, EventArgs.Empty);
}

/// <summary>What the Chat window knows about the host's Local Store.</summary>
public enum ConversationStorage
{
    /// <summary>The host has not answered yet; conversations are kept in memory and saved when it does.</summary>
    Unknown,

    /// <summary>The host saves conversations.</summary>
    Available,

    /// <summary>The host cannot save conversations (the <c>localStore</c> feature is off, the store failed, or the host is older than contract 1.3): they last until Visual Studio closes.</summary>
    Unavailable,
}

/// <summary>
/// The saved-conversation half of the Chat window's view model (work item 2.2): the list loaded from the host, newest first and
/// paged on scroll, switching to a conversation reads its messages and pins from the host, rename and delete follow it, and
/// when the host cannot save conversations the list falls back to the in-memory behaviour with a one-line notice. Every RPC
/// call runs on the thread pool; only the final updates of bound properties run on the UI thread.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>How many conversations one page of the list holds.</summary>
    public const int ConversationPageSize = 50;

    private static readonly TimeSpan ConversationCallTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private ConversationEvents? _conversationEvents;
    private ConversationStorage _storage;
    private string _conversationsNotice = string.Empty;
    private bool _hasMoreConversations;
    private bool _isLoadingConversations;
    private int _conversationsVersion;
    private int _listedFromHost;

    /// <summary>Gets what the window knows about the host's Local Store.</summary>
    public ConversationStorage Storage
    {
        get => _storage;
        private set => SetProperty(ref _storage, value);
    }

    /// <summary>Gets the one-line notice under the conversation list: why conversations are not being saved, or that a change was not saved. Empty when there is nothing to say.</summary>
    public string ConversationsNotice
    {
        get => _conversationsNotice;
        private set
        {
            if (SetProperty(ref _conversationsNotice, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasConversationsNotice));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="ConversationsNotice"/> has text.</summary>
    public bool HasConversationsNotice => _conversationsNotice.Length > 0;

    /// <summary>Gets a value indicating whether the host has more conversations than the list holds (it loads the next page when scrolled to the end).</summary>
    public bool HasMoreConversations
    {
        get => _hasMoreConversations;
        private set
        {
            if (SetProperty(ref _hasMoreConversations, value))
            {
                LoadMoreConversationsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets a value indicating whether the list is being read from the host.</summary>
    public bool IsLoadingConversations
    {
        get => _isLoadingConversations;
        private set
        {
            if (SetProperty(ref _isLoadingConversations, value))
            {
                LoadMoreConversationsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the command that reads the next page of the list from the host.</summary>
    public DelegateCommand LoadMoreConversationsCommand { get; private set; } = null!;

    /// <summary>Gets the command that reads the current conversation again after it could not be read.</summary>
    public DelegateCommand RetryLoadConversationCommand { get; private set; } = null!;

    /// <summary>Gets the command that opens a cited span in the editor; its parameter is the <see cref="CitationItem"/>.</summary>
    public DelegateCommand OpenCitationCommand { get; private set; } = null!;

    /// <summary>
    /// Reads the first page of the list from the host and merges it with the conversations in memory: a conversation the
    /// window already holds keeps its transcript, the others are added without messages until they are opened. A host that
    /// cannot save conversations leaves the list as it is and shows <see cref="ConversationsNotice"/>.
    /// </summary>
    /// <returns>A task that completes when the list shows the host's conversations or the notice. Call on the UI thread.</returns>
    public async Task LoadConversationsAsync()
    {
        if (_disposed || !IsHostUsable)
        {
            return;
        }

        var version = Interlocked.Increment(ref _conversationsVersion);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        IsLoadingConversations = true;
        await TaskScheduler.Default;
        ConversationPage? page = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            page = await _connection.GetProxy<IConversationService>()
                .ListConversationsAsync(new ConversationListRequest { Offset = 0, Limit = ConversationPageSize }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A host without saved conversations is a notice, not a failure of the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Debug("chat.conversations.list.failed", "Listing the saved conversations failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (version != Volatile.Read(ref _conversationsVersion) || _disposed)
        {
            return; // A newer read is on its way.
        }

        IsLoadingConversations = false;
        if (page is null)
        {
            ReportStorageFailure(failure!, listing: true);
            return;
        }

        Storage = ConversationStorage.Available;
        ConversationsNotice = string.Empty;
        _listedFromHost = 0;
        MergePage(page, reset: true);
    }

    /// <summary>Reads the next page of the list from the host and adds the conversations it has not listed yet. Call on the UI thread.</summary>
    /// <returns>A task that completes when the page has been added.</returns>
    public async Task LoadMoreConversationsAsync()
    {
        if (_disposed || !IsHostUsable || !HasMoreConversations || IsLoadingConversations || Storage != ConversationStorage.Available)
        {
            return;
        }

        var version = Interlocked.Increment(ref _conversationsVersion);
        var offset = _listedFromHost;
        IsLoadingConversations = true;
        await TaskScheduler.Default;
        ConversationPage? page = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            page = await _connection.GetProxy<IConversationService>()
                .ListConversationsAsync(new ConversationListRequest { Offset = offset, Limit = ConversationPageSize }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // The next page is optional; the failure is shown as a notice.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Debug("chat.conversations.more.failed", "Listing more saved conversations failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        IsLoadingConversations = false;
        if (version != Volatile.Read(ref _conversationsVersion) || _disposed)
        {
            return; // The list was read again meanwhile.
        }

        if (page is null)
        {
            ReportStorageFailure(failure!, listing: false);
            return;
        }

        MergePage(page, reset: false);
    }

    /// <summary>Starts renaming a conversation chosen in the list (it becomes the current one). Call on the UI thread.</summary>
    /// <param name="conversation">The conversation.</param>
    public void RenameConversation(ChatConversation? conversation)
    {
        if (conversation is null || _disposed || !Conversations.Contains(conversation))
        {
            return;
        }

        SwitchTo(conversation);
        BeginRename();
    }

    /// <summary>Opens a cited span in the editor (the window supplies the action).</summary>
    /// <param name="item">The citation.</param>
    private void OpenCitation(CitationItem? item)
    {
        if (item is not null && item.FilePath.Length > 0)
        {
            _openCitation?.Invoke(item.FilePath, item.StartLine);
        }
    }

    /// <summary>Puts the host's page into the list: new conversations are added without messages, known ones take the host's title and order.</summary>
    private void MergePage(ConversationPage page, bool reset)
    {
        HasMoreConversations = page.HasMore;
        _listedFromHost += page.Items.Length;
        var known = Conversations.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var fromHost = new List<ChatConversation>();
        foreach (var summary in page.Items.Where(s => s is not null && !string.IsNullOrEmpty(s.Id)))
        {
            if (known.TryGetValue(summary.Id, out var existing))
            {
                existing.IsPersisted = true;
                existing.UpdatedUtc = summary.UpdatedUtc;
                if (!existing.IsLoaded)
                {
                    existing.StoredMessageCount = summary.MessageCount;
                }

                existing.SetTitleFromHost(summary.Title);
                fromHost.Add(existing);
            }
            else
            {
                fromHost.Add(new ChatConversation(summary));
            }
        }

        if (!reset)
        {
            foreach (var conversation in fromHost.Where(c => !Conversations.Contains(c)))
            {
                Conversations.Add(conversation);
            }

            return;
        }

        // The window's own conversations that the host does not list stay: the ones it never saved (at the top, as they were) and
        // the ones already open or beyond this page (after the page).
        var listed = new HashSet<ChatConversation>(fromHost);
        var local = Conversations.Where(c => !listed.Contains(c) && (!c.IsPersisted || ReferenceEquals(c, CurrentConversation) || c.IsLoaded)).ToList();
        var desired = local.Where(c => !c.IsPersisted).Concat(fromHost).Concat(local.Where(c => c.IsPersisted)).ToList();
        SyncList(desired);
    }

    /// <summary>Makes <see cref="Conversations"/> hold exactly the given conversations in the given order, changing as little as it can (the open one is never removed).</summary>
    private void SyncList(List<ChatConversation> desired)
    {
        for (var i = Conversations.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Conversations[i]) && !ReferenceEquals(Conversations[i], CurrentConversation))
            {
                Conversations.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var wanted = desired[i];
            var at = Conversations.IndexOf(wanted);
            if (at < 0)
            {
                Conversations.Insert(Math.Min(i, Conversations.Count), wanted);
            }
            else if (at != i && i < Conversations.Count)
            {
                Conversations.Move(at, i);
            }
        }
    }

    /// <summary>Starts reading a conversation the host listed when the window opens it.</summary>
    private void LoadIfNeeded(ChatConversation conversation)
    {
        if (conversation.IsPersisted && !conversation.IsLoaded && !conversation.IsLoading)
        {
            Run(() => LoadConversationAsync(conversation));
        }
    }

    /// <summary>Reads a conversation's messages and pins from the host and shows them.</summary>
    private async Task LoadConversationAsync(ChatConversation conversation)
    {
        if (conversation.IsLoading || conversation.IsLoaded)
        {
            return;
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (conversation.IsLoading || conversation.IsLoaded || _disposed)
        {
            return;
        }

        conversation.IsLoading = true;
        conversation.LoadError = string.Empty;
        UpdateCommands();

        await TaskScheduler.Default;
        ConversationDetail? detail = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            detail = await _connection.GetProxy<IConversationService>().GetConversationAsync(conversation.Id, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Shown in the transcript's place, with a way to try again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.conversation.load.failed", "Opening a saved conversation failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (_disposed)
        {
            return;
        }

        conversation.IsLoading = false;
        if (failure is not null)
        {
            conversation.LoadError = ChatStrings.Format(ChatStrings.ConversationLoadFailedFormat, failure.Message);
        }
        else if (detail is null)
        {
            // Deleted elsewhere (another Visual Studio, or Delete all): it is gone from the list too.
            RemoveGone(conversation);
        }
        else
        {
            ApplyDetail(conversation, detail);
        }

        UpdateRetry();
        UpdateCommands();
        if (detail is not null && failure is null && Mentions.IsEnabled)
        {
            await Mentions.RestorePinsAsync(conversation, detail.Pins ?? []).ConfigureAwait(true);
        }
    }

    private void ApplyDetail(ChatConversation conversation, ConversationDetail detail)
    {
        var listed = ModelPicker.Models.ToList();
        conversation.Turns.Clear();
        foreach (var message in detail.Messages ?? [])
        {
            if (message is null)
            {
                continue;
            }

            if (message.Role == ChatRole.User)
            {
                conversation.Turns.Add(ChatTurnViewModel.FromSavedUser(message.Content ?? string.Empty));
            }
            else if (message.Role == ChatRole.Assistant)
            {
                var text = message.Content ?? string.Empty;
                var stopped = message.Cancelled; // The turn shows its own "Stopped." note.

                var hasModel = !string.IsNullOrEmpty(message.ModelId);
                var header = hasModel ? ModelOption.ReplyHeaderFor(listed, message.ProviderId, message.ModelId!) : ChatStrings.SavedReplyHeader;
                var pickerId = !hasModel ? null : string.IsNullOrEmpty(message.ProviderId) ? message.ModelId : message.ProviderId + "/" + message.ModelId;
                var turn = ChatTurnViewModel.FromSavedReply(header, pickerId, text, stopped, CitationItem.From(message.Citations, OpenCitationCommand));
                turn.RetryCommand = RetryCommand;
                conversation.Turns.Add(turn);
            }
        }

        if (detail.Conversation is { } summary && summary.UpdatedUtc > DateTime.MinValue)
        {
            conversation.UpdatedUtc = summary.UpdatedUtc;
        }

        conversation.IsLoaded = true;
    }

    /// <summary>Takes a conversation the host no longer has out of the list.</summary>
    private void RemoveGone(ChatConversation conversation)
    {
        var index = Conversations.IndexOf(conversation);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(conversation, CurrentConversation))
        {
            if (Conversations.Count == 1)
            {
                Conversations.Insert(0, new ChatConversation());
                index++;
            }

            SwitchTo(Conversations[index + 1 < Conversations.Count ? index + 1 : index - 1]);
        }

        Conversations.Remove(conversation);
    }

    /// <summary>Forgets every saved conversation the window listed (Delete all on the Privacy page) and shows a new, empty one.</summary>
    private void ResetAfterDeleteAll()
    {
        if (_disposed)
        {
            return;
        }

        var fresh = new ChatConversation();
        Conversations.Insert(0, fresh);
        SwitchTo(fresh);
        for (var i = Conversations.Count - 1; i >= 1; i--)
        {
            Conversations.RemoveAt(i);
        }

        _listedFromHost = 0;
        HasMoreConversations = false;
        UpdateCommands();
        if (IsOutboundLogEnabled)
        {
            Run(OutboundLog.RefreshAsync);
        }
    }

    /// <summary>What to do with the host's answer to a call that failed: a lost host is waited out, anything else says conversations are not saved.</summary>
    private void ReportStorageFailure(RpcFailure failure, bool listing)
    {
        if (failure.HostLost)
        {
            return; // The window reads the list again when the host is back.
        }

        if (failure.Code == PillionErrorCode.PolicyDenied)
        {
            Storage = ConversationStorage.Unavailable;
            ConversationsNotice = ChatStrings.ConversationsNotSavedOff;
        }
        else if (listing || failure.Code == PillionErrorCode.StorageUnavailable)
        {
            Storage = ConversationStorage.Unavailable;
            ConversationsNotice = ChatStrings.Format(ChatStrings.ConversationsNotSavedFormat, OneLine(failure.Message));
        }
        else
        {
            ConversationsNotice = ChatStrings.Format(ChatStrings.ConversationSaveFailedFormat, OneLine(failure.Message));
        }

        HasMoreConversations = false;
    }

    private static string OneLine(string? message)
    {
        var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 140 ? text : text.Substring(0, 139) + "…";
    }

    /// <summary>Whether the window should keep the host informed of changes to its conversations.</summary>
    private bool CanSaveConversations => Storage != ConversationStorage.Unavailable;

    private void OnConversationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (ChatConversation conversation in e.NewItems)
        {
            conversation.Pins.CollectionChanged += (_, _) => OnPinsChanged(conversation);
        }
    }

    /// <summary>The conversation's pins changed on the UI thread: save them when the host has the conversation.</summary>
    private void OnPinsChanged(ChatConversation conversation)
    {
        if (conversation.IsRestoringPins || !conversation.IsPersisted || !CanSaveConversations || _disposed)
        {
            return;
        }

        Run(() => SavePinsAsync(conversation));
    }

    private async Task SavePinsAsync(ChatConversation conversation)
    {
        var version = Interlocked.Increment(ref conversation.PinsSaveVersion);
        var pins = conversation.Pins.Select(p => p.ToSaved()).ToArray(); // On the UI thread.
        await TaskScheduler.Default;
        RpcFailure? failure = null;
        var stored = true;
        await _storeGate.WaitAsync(_disposal.Token).ConfigureAwait(false);
        try
        {
            if (version != Volatile.Read(ref conversation.PinsSaveVersion))
            {
                return; // A newer change follows and saves the whole list.
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            stored = await _connection.GetProxy<IConversationService>().SetPinsAsync(conversation.Id, pins, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A pin that cannot be saved is a notice; the pin still works in this session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.pins.save.failed", "Saving a conversation's pins failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }
        finally
        {
            _storeGate.Release();
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (failure is not null)
        {
            ReportStorageFailure(failure, listing: false);
        }
        else if (!stored)
        {
            conversation.IsPersisted = false; // The host does not have it: the next message saves it again.
        }
    }

    private async Task RenameOnHostAsync(ChatConversation conversation, string title)
    {
        await TaskScheduler.Default;
        RpcFailure? failure = null;
        var found = true;
        await _storeGate.WaitAsync(_disposal.Token).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            found = await _connection.GetProxy<IConversationService>().RenameConversationAsync(conversation.Id, title, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A rename that cannot be saved is a notice; the new name shows in this session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.rename.failed", "Renaming a saved conversation failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }
        finally
        {
            _storeGate.Release();
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (failure is not null)
        {
            ReportStorageFailure(failure, listing: false);
        }
        else if (!found)
        {
            conversation.IsPersisted = false;
        }
    }

    private async Task DeleteOnHostAsync(string conversationId)
    {
        await TaskScheduler.Default;
        RpcFailure? failure = null;
        await _storeGate.WaitAsync(_disposal.Token).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            await _connection.GetProxy<IConversationService>().DeleteConversationAsync(conversationId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A delete that cannot be saved is a notice; the conversation may come back when the list is read again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Warning("chat.delete.failed", "Deleting a saved conversation failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }
        finally
        {
            _storeGate.Release();
        }

        if (failure is not null)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            ConversationsNotice = ChatStrings.Format(ChatStrings.ConversationSaveFailedFormat, OneLine(failure.Message));
        }
    }

    /// <summary>
    /// Before the first message of a conversation the host does not have yet: creates it when it already has a name of its own or
    /// pins (the host names the others from their first message, and saves their pins afterwards). Best effort: the message goes
    /// either way, and the host creates a conversation it does not know.
    /// </summary>
    private async Task EnsureStoredAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (!context.NeedsCreate)
        {
            return;
        }

        await _storeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var service = _connection.GetProxy<IConversationService>();
            await service.CreateConversationAsync(new ConversationCreateRequest { Id = context.ConversationId, Title = context.CustomTitle ?? string.Empty }, cancellationToken).ConfigureAwait(false);
            if (context.SavedPins.Length > 0)
            {
                await service.SetPinsAsync(context.ConversationId, context.SavedPins, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The message goes without it; the host creates the conversation when the message arrives.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("chat.conversation.create.failed", "Creating the conversation before its first message failed.", ("error", ex.GetType().Name));
        }
        finally
        {
            _storeGate.Release();
        }
    }

    /// <summary>After a reply ended: the host has the conversation now, and it moved to the top of the list.</summary>
    private void AfterReply(ChatConversation? conversation, ChatTurnState state)
    {
        if (conversation is null || _disposed || Storage == ConversationStorage.Unavailable)
        {
            return;
        }

        if (!conversation.IsPersisted)
        {
            if (state is ChatTurnState.Complete or ChatTurnState.Stopped)
            {
                conversation.IsPersisted = true;
                Run(() => AdoptHostTitleAsync(conversation));
            }
            else
            {
                Run(() => ConfirmStoredAsync(conversation));
            }
        }

        if (conversation.IsPersisted && Storage == ConversationStorage.Available)
        {
            var at = Conversations.IndexOf(conversation);
            if (at > 0)
            {
                Conversations.Move(at, 0);
            }
        }
    }

    /// <summary>
    /// After the first reply: takes the title the host named the conversation (60 characters of the first line) in place of the
    /// window's own shorter one. A conversation the developer named is left alone: the host has their name.
    /// </summary>
    private async Task AdoptHostTitleAsync(ChatConversation conversation)
    {
        await TaskScheduler.Default;
        ConversationDetail? detail;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            detail = await _connection.GetProxy<IConversationService>().GetConversationAsync(conversation.Id, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // The window's own title stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("chat.conversation.title.failed", "Reading the host's title for the conversation failed.", ("error", ex.GetType().Name));
            return;
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (!_disposed && !conversation.RenamedByUser && detail?.Conversation is { } summary)
        {
            conversation.SetTitleFromHost(summary.Title);
        }
    }

    /// <summary>Asks the host whether it has a conversation whose reply did not complete (the message may or may not have been saved).</summary>
    private async Task ConfirmStoredAsync(ChatConversation conversation)
    {
        await TaskScheduler.Default;
        bool? has = null;
        ConversationDetail? found = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            timeout.CancelAfter(ConversationCallTimeout);
            found = await _connection.GetProxy<IConversationService>().GetConversationAsync(conversation.Id, timeout.Token).ConfigureAwait(false);
            has = found is not null;
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Not knowing leaves the conversation local; the next reply tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("chat.conversation.confirm.failed", "Checking whether the host saved the conversation failed.", ("error", ex.GetType().Name));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (has == true && !_disposed)
        {
            conversation.IsPersisted = true;
            if (!conversation.RenamedByUser && found?.Conversation is { } summary)
            {
                conversation.SetTitleFromHost(summary.Title);
            }

            if (conversation.Pins.Count > 0)
            {
                OnPinsChanged(conversation);
            }
        }
    }

    /// <summary>The conversation was cleared: the host's copy goes, and the window's is a new, local one with the same id.</summary>
    private void ForgetOnHostAfterClear(ChatConversation conversation)
    {
        if (!conversation.IsPersisted)
        {
            return;
        }

        var id = conversation.Id;
        var save = CanSaveConversations;
        conversation.ResetAfterClear();
        if (save)
        {
            Run(() => DeleteOnHostAsync(id));
        }
    }
}
