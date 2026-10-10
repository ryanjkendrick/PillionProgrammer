using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>What a picker entry is.</summary>
public enum MentionPickerItemKind
{
    /// <summary>A mention kind (<c>@file</c>, <c>@folder</c>, <c>@selection</c>, <c>@errors</c>, <c>@clipboard</c>).</summary>
    Kind,

    /// <summary>A file of the solution or folder.</summary>
    File,

    /// <summary>A folder of the solution or folder.</summary>
    Folder,

    /// <summary>One of the paths an ambiguous mention matched.</summary>
    Candidate,
}

/// <summary>One entry of the mention picker.</summary>
public sealed class MentionPickerItem : ObservableObject
{
    private bool _isSelected;

    /// <summary>Initializes a new instance of the <see cref="MentionPickerItem"/> class.</summary>
    /// <param name="itemKind">What the entry is.</param>
    /// <param name="mentionKind">The mention kind it pins (<c>file</c>, <c>folder</c>, ...).</param>
    /// <param name="label">The text shown (<c>@errors</c>, a file name).</param>
    /// <param name="detail">The secondary text (a description, a relative path).</param>
    /// <param name="value">The full path for a file, folder or candidate; empty for a kind.</param>
    public MentionPickerItem(MentionPickerItemKind itemKind, string mentionKind, string label, string detail, string value)
    {
        ItemKind = itemKind;
        MentionKind = mentionKind;
        Label = label;
        Detail = detail;
        Value = value;
    }

    /// <summary>Gets what the entry is.</summary>
    public MentionPickerItemKind ItemKind { get; }

    /// <summary>Gets the mention kind the entry pins.</summary>
    public string MentionKind { get; }

    /// <summary>Gets the text shown.</summary>
    public string Label { get; }

    /// <summary>Gets the secondary text.</summary>
    public string Detail { get; }

    /// <summary>Gets the full path for a file, folder or candidate; empty for a kind.</summary>
    public string Value { get; }

    /// <summary>Gets the name a screen reader announces.</summary>
    public string AutomationName => Detail.Length == 0 ? Label : Label + ", " + Detail;

    /// <summary>Gets or sets a value indicating whether the entry is the highlighted one.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Gets the mention text resolved for this entry, for example <c>@errors</c> or <c>@file:C:\src\Program.cs</c>.</summary>
    public string MentionText => ItemKind == MentionPickerItemKind.Kind ? "@" + MentionKind : "@" + MentionKind + ":" + Value;

    /// <inheritdoc/>
    public override string ToString() => AutomationName;
}

/// <summary>
/// The mention picker shown at the caret while an <c>@</c> mention is typed (design section 7: "the picker shows matches as
/// you type"): the kinds first, then up to <see cref="MentionFileMatcher.MaxMatches"/> fuzzy file matches. It doubles as the
/// sub-picker that lists the paths an ambiguous mention matched. Keyboard: Up and Down move, Enter and Tab pick, Esc closes.
/// Call on the UI thread.
/// </summary>
public sealed class MentionPickerViewModel : ObservableObject
{
    private bool _isOpen;
    private int _selectedIndex = -1;
    private string _title = string.Empty;
    private bool _isCandidates;
    private MentionToken? _token;
    private int? _dismissedAt;

    /// <summary>Gets the entries.</summary>
    public ObservableCollection<MentionPickerItem> Items { get; } = [];

    /// <summary>Gets a value indicating whether the picker is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>Gets the index of the highlighted entry, or -1.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                for (var i = 0; i < Items.Count; i++)
                {
                    Items[i].IsSelected = i == value;
                }

                OnPropertyChanged(nameof(SelectedItem));
            }
        }
    }

    /// <summary>Gets the highlighted entry, or <see langword="null"/>.</summary>
    public MentionPickerItem? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    /// <summary>Gets the line above the entries: a hint, or the question of the sub-picker.</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Gets a value indicating whether the picker lists the candidates of an ambiguous mention.</summary>
    public bool IsCandidates
    {
        get => _isCandidates;
        private set => SetProperty(ref _isCandidates, value);
    }

    /// <summary>Gets the mention being typed that the picker was opened for, or <see langword="null"/> for the sub-picker.</summary>
    public MentionToken? Token => _token;

    /// <summary>
    /// Updates the picker for the input and caret: opens it with the matching entries while the caret is in a mention, closes
    /// it otherwise. A mention closed with Esc stays closed until the caret leaves it.
    /// </summary>
    /// <param name="text">The input text.</param>
    /// <param name="caret">The caret index.</param>
    /// <param name="files">The solution or folder file list.</param>
    public void Update(string? text, int caret, WorkspaceFileList files)
    {
        var token = MentionSyntax.FindAt(text, caret);
        if (token is null)
        {
            _dismissedAt = null;
            if (!IsCandidates)
            {
                Close();
            }

            return;
        }

        if (_dismissedAt == token.Start)
        {
            return;
        }

        _dismissedAt = null;
        _token = token;
        IsCandidates = false;
        var parsed = token.Parsed;
        var items = new List<MentionPickerItem>();
        var list = files ?? WorkspaceFileList.Empty;
        if (!parsed.HasKindSeparator)
        {
            var typed = token.Query.ToLowerInvariant();
            items.AddRange(MentionSyntax.Kinds.Where(k => k.StartsWith(typed, StringComparison.Ordinal)).Select(KindItem));
            items.AddRange(MentionFileMatcher.MatchFiles(list.Files, list.Roots, token.Query).Select(m => PathItem(MentionPickerItemKind.File, MentionSyntax.File, m)));
            Title = token.Query.Length == 0 ? ChatStrings.MentionPickerHint : ChatStrings.MentionPickerMatchesTitle;
        }
        else if (parsed.Kind == MentionSyntax.File)
        {
            items.AddRange(MentionFileMatcher.MatchFiles(list.Files, list.Roots, parsed.Value).Select(m => PathItem(MentionPickerItemKind.File, MentionSyntax.File, m)));
            Title = parsed.Value.Length == 0 ? ChatStrings.MentionPickerFileHint : ChatStrings.MentionPickerMatchesTitle;
        }
        else if (parsed.Kind == MentionSyntax.Folder)
        {
            items.AddRange(MentionFileMatcher.MatchFolders(list.Files, list.Roots, parsed.Value).Select(m => PathItem(MentionPickerItemKind.Folder, MentionSyntax.Folder, m)));
            Title = parsed.Value.Length == 0 ? ChatStrings.MentionPickerFolderHint : ChatStrings.MentionPickerMatchesTitle;
        }
        else
        {
            items.Add(KindItem(parsed.Kind));
            Title = ChatStrings.MentionPickerMatchesTitle;
        }

        if (items.Count == 0 && token.Query.Length > 0 && !(parsed.HasKindSeparator && parsed.Value.Length == 0))
        {
            Title = ChatStrings.MentionPickerNoMatches;
        }

        Fill(items);
        IsOpen = true;
    }

    /// <summary>Opens the sub-picker with the paths an ambiguous mention matched.</summary>
    /// <param name="kind">The kind the mention resolved as (<c>file</c> or <c>folder</c>).</param>
    /// <param name="mention">The mention that was ambiguous, for the title.</param>
    /// <param name="candidates">The full paths.</param>
    /// <param name="roots">The workspace roots, for relative paths.</param>
    public void ShowCandidates(string kind, string mention, IReadOnlyList<string> candidates, IReadOnlyList<string> roots)
    {
        _token = null;
        _dismissedAt = null;
        IsCandidates = true;
        var mentionKind = kind == MentionSyntax.Folder ? MentionSyntax.Folder : MentionSyntax.File;
        Title = ChatStrings.Format(ChatStrings.MentionCandidatesTitleFormat, mention);
        Fill(candidates.Select(c => PathItem(MentionPickerItemKind.Candidate, mentionKind, new PathMatch(c, MentionFileMatcher.Relative(c, roots), 0))));
        IsOpen = true;
    }

    /// <summary>Moves the highlight, wrapping at either end.</summary>
    /// <param name="delta">+1 for Down, -1 for Up.</param>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var next = SelectedIndex < 0 ? (delta > 0 ? 0 : Items.Count - 1) : (SelectedIndex + delta + Items.Count) % Items.Count;
        SelectedIndex = next;
    }

    /// <summary>Highlights an entry (a mouse click).</summary>
    /// <param name="item">The entry.</param>
    public void Select(MentionPickerItem item)
    {
        var index = Items.IndexOf(item);
        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    /// <summary>Closes the picker (Esc); the mention being typed stays closed until the caret leaves it.</summary>
    public void Dismiss()
    {
        _dismissedAt = _token?.Start;
        Close();
    }

    /// <summary>Closes the picker.</summary>
    public void Close()
    {
        IsCandidates = false;
        IsOpen = false;
        _token = null;
        Items.Clear();
        SelectedIndex = -1;
    }

    private static MentionPickerItem KindItem(string kind) => new(
        MentionPickerItemKind.Kind,
        kind,
        "@" + kind,
        kind switch
        {
            MentionSyntax.File => ChatStrings.MentionKindFile,
            MentionSyntax.Folder => ChatStrings.MentionKindFolder,
            MentionSyntax.Selection => ChatStrings.MentionKindSelection,
            MentionSyntax.Errors => ChatStrings.MentionKindErrors,
            _ => ChatStrings.MentionKindClipboard,
        },
        string.Empty);

    private static MentionPickerItem PathItem(MentionPickerItemKind itemKind, string mentionKind, PathMatch match) =>
        new(itemKind, mentionKind, match.Name, match.RelativePath, match.FullPath);

    private void Fill(IEnumerable<MentionPickerItem> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        _selectedIndex = -1;
        SelectedIndex = Items.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedItem));
    }
}
