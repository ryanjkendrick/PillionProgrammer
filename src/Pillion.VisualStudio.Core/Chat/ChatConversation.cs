using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One conversation in the chat window's list: a title and its transcript. A conversation lives in memory while Visual Studio
/// runs and, while Pillion Host's Local Store is available (work item 2.2), is also saved by the host: <see cref="IsPersisted"/>
/// says the host has it, and one listed from the host starts without its messages (<see cref="IsLoaded"/> is false) until the
/// window opens it.
/// </summary>
public sealed class ChatConversation : ObservableObject
{
    /// <summary>The longest title taken from a first message, in characters.</summary>
    public const int MaxAutomaticTitleLength = 48;

    private string _title;
    private bool _hasCustomTitle;
    private bool _isPersisted;
    private bool _renamedByUser;
    private bool _isLoaded = true;
    private bool _isLoading;
    private int _storedMessageCount;
    private string _loadError = string.Empty;

    /// <summary>The version of the latest request to save this conversation's pins; a save that finds a newer one skips itself.</summary>
    internal int PinsSaveVersion;

    /// <summary>Initializes a new instance of the <see cref="ChatConversation"/> class.</summary>
    public ChatConversation()
    {
        Id = Guid.NewGuid().ToString("N");
        _title = ChatStrings.NewConversationTitle;
        Turns.CollectionChanged += OnTurnsChanged;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatConversation"/> class from a conversation the host lists. Its title is
    /// the host's (it no longer follows the first message), and its messages and pins load when the conversation is opened.
    /// </summary>
    /// <param name="summary">The host's summary.</param>
    public ChatConversation(ConversationSummary summary)
    {
        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        Id = summary.Id;
        _title = TitleFromHost(summary.Title);
        _hasCustomTitle = true;
        _isPersisted = true;
        _isLoaded = false;
        _storedMessageCount = Math.Max(0, summary.MessageCount);
        UpdatedUtc = summary.UpdatedUtc;
        Turns.CollectionChanged += OnTurnsChanged;
    }

    /// <summary>Gets the conversation's id: the id the host stores it under when it is saved.</summary>
    public string Id { get; }

    /// <summary>Gets when the conversation last changed, as the host last said (UTC); the default for one the host has not listed.</summary>
    public DateTime UpdatedUtc { get; internal set; }

    /// <summary>Gets a value indicating whether the host has this conversation (its messages are being saved, and its title, pins and deletion follow it).</summary>
    public bool IsPersisted
    {
        get => _isPersisted;
        internal set => SetProperty(ref _isPersisted, value);
    }

    /// <summary>Gets a value indicating whether <see cref="Turns"/> and <see cref="Pins"/> hold the conversation (false for one listed by the host and not opened yet).</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        internal set
        {
            if (SetProperty(ref _isLoaded, value))
            {
                if (value)
                {
                    _storedMessageCount = 0; // The transcript is the truth now.
                }

                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Gets a value indicating whether the messages are being read from the host.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        internal set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Gets why the messages could not be read from the host, or empty.</summary>
    public string LoadError
    {
        get => _loadError;
        internal set
        {
            if (SetProperty(ref _loadError, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasLoadError));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="LoadError"/> has text.</summary>
    public bool HasLoadError => _loadError.Length > 0;

    /// <summary>Gets a value indicating whether the pins are being put back (the host's pins show before they are read again).</summary>
    internal bool IsRestoringPins { get; set; }

    /// <summary>Gets or sets how many messages the host said the conversation has, while it is not loaded.</summary>
    internal int StoredMessageCount
    {
        get => _storedMessageCount;
        set
        {
            _storedMessageCount = Math.Max(0, value);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    /// <summary>Gets the title: the developer's name for it, else the start of the first message, else "New conversation".</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets a value indicating whether the developer renamed the conversation (its title no longer follows the first message).</summary>
    public bool HasCustomTitle
    {
        get => _hasCustomTitle;
        private set => SetProperty(ref _hasCustomTitle, value);
    }

    /// <summary>Gets the transcript, oldest first.</summary>
    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];

    /// <summary>
    /// Gets the context pinned to the conversation with <c>@</c>-mentions, in the order they were pinned. They are sent with
    /// every message of the conversation and kept with it in memory (design section 7: pins persist for the conversation).
    /// </summary>
    public ObservableCollection<MentionPin> Pins { get; } = [];

    /// <summary>Gets a value indicating whether the conversation has no messages (one the host lists but the window has not opened counts the host's messages).</summary>
    public bool IsEmpty => Turns.Count == 0 && _storedMessageCount == 0;

    /// <summary>Gets the name a screen reader announces for the conversation in the list.</summary>
    public string AutomationName => ChatStrings.Format(ChatStrings.ConversationItemNameFormat, Title, Turns.Count > 0 ? Turns.Count : _storedMessageCount);

    /// <summary>Makes a title from the start of a message: its first line, cut at a word boundary and ellipsised.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The title, or <see langword="null"/> when the message has no visible text.</returns>
    public static string? TitleFrom(string? message)
    {
        var line = (message ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return null;
        }

        if (line.Length <= MaxAutomaticTitleLength)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', MaxAutomaticTitleLength - 1);
        if (cut < MaxAutomaticTitleLength / 2)
        {
            cut = MaxAutomaticTitleLength - 1;
        }

        return line.Substring(0, cut).TrimEnd() + "…";
    }

    /// <summary>Takes the title the host stores (for a conversation the host listed, or after the host named one). Call on the UI thread.</summary>
    /// <param name="title">The host's title; empty keeps the current one.</param>
    internal void SetTitleFromHost(string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            _hasCustomTitle = true;
            Title = TitleFromHost(title!);
        }
    }

    /// <summary>
    /// Makes a cleared conversation local again: the host's copy is deleted, so its title follows the next first message again
    /// (unless the developer renamed it). Call on the UI thread.
    /// </summary>
    internal void ResetAfterClear()
    {
        IsPersisted = false;
        if (!_renamedByUser)
        {
            _hasCustomTitle = false;
            OnPropertyChanged(nameof(HasCustomTitle));
            Title = ChatStrings.NewConversationTitle;
        }
    }

    private static string TitleFromHost(string? title) =>
        string.IsNullOrWhiteSpace(title) ? ChatStrings.NewConversationTitle : title!.Trim();

    /// <summary>Renames the conversation. Call on the UI thread.</summary>
    /// <param name="title">The new title; blank keeps the current one.</param>
    /// <returns><see langword="true"/> when the title changed.</returns>
    public bool Rename(string? title)
    {
        var value = title?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        HasCustomTitle = true;
        _renamedByUser = true;
        var changed = !string.Equals(Title, value, StringComparison.Ordinal);
        Title = value!;
        return changed;
    }

    /// <inheritdoc/>
    public override string ToString() => Title;

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AutomationName));
        if (HasCustomTitle || IsLoading)
        {
            return;
        }

        // The title follows the first message until the developer renames the conversation; Clear resets it.
        var first = Turns.FirstOrDefault(t => t.IsUser);
        Title = (first is null ? null : TitleFrom(first.Text)) ?? ChatStrings.NewConversationTitle;
    }
}
