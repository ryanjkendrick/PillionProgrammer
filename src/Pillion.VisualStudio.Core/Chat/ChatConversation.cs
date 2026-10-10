using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One conversation in the chat window's list: a title and its transcript. Conversations are kept in memory for the
/// Visual Studio session; persistence arrives with the encrypted Local Store in phase 2 (phase 1 plan, 2026-10-08).
/// </summary>
public sealed class ChatConversation : ObservableObject
{
    /// <summary>The longest title taken from a first message, in characters.</summary>
    public const int MaxAutomaticTitleLength = 48;

    private string _title;
    private bool _hasCustomTitle;

    /// <summary>Initializes a new instance of the <see cref="ChatConversation"/> class.</summary>
    public ChatConversation()
    {
        Id = Guid.NewGuid().ToString("N");
        _title = ChatStrings.NewConversationTitle;
        Turns.CollectionChanged += OnTurnsChanged;
    }

    /// <summary>Gets the conversation's id (for this session only).</summary>
    public string Id { get; }

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

    /// <summary>Gets a value indicating whether the conversation has no turns.</summary>
    public bool IsEmpty => Turns.Count == 0;

    /// <summary>Gets the name a screen reader announces for the conversation in the list.</summary>
    public string AutomationName => ChatStrings.Format(ChatStrings.ConversationItemNameFormat, Title, Turns.Count);

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
        if (HasCustomTitle)
        {
            return;
        }

        // The title follows the first message until the developer renames the conversation; Clear resets it.
        var first = Turns.FirstOrDefault(t => t.IsUser);
        Title = (first is null ? null : TitleFrom(first.Text)) ?? ChatStrings.NewConversationTitle;
    }
}
