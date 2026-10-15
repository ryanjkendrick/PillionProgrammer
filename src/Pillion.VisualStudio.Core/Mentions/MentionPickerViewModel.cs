using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>What a picker entry is.</summary>
public enum MentionPickerItemKind
{
    /// <summary>A mention kind (<c>@file</c>, <c>@symbol</c>, <c>@git</c>, ...).</summary>
    Kind,

    /// <summary>A file of the solution or folder.</summary>
    File,

    /// <summary>A folder of the solution or folder.</summary>
    Folder,

    /// <summary>One of the matches an ambiguous mention offered.</summary>
    Candidate,

    /// <summary>A symbol the host found in the index (work item 2.13).</summary>
    Symbol,

    /// <summary>A documentation file (work item 2.13).</summary>
    Doc,

    /// <summary>One option of a kind that takes one (<c>@git:staged</c>, <c>@output:debug</c>), or the typed value itself.</summary>
    Option,
}

/// <summary>One entry of the mention picker.</summary>
public sealed class MentionPickerItem : ObservableObject
{
    private bool _isSelected;
    private bool _isFirstInGroup;

    /// <summary>Initializes a new instance of the <see cref="MentionPickerItem"/> class.</summary>
    /// <param name="itemKind">What the entry is.</param>
    /// <param name="mentionKind">The mention kind it pins (<c>file</c>, <c>folder</c>, ...).</param>
    /// <param name="label">The text shown (<c>@errors</c>, a file name).</param>
    /// <param name="detail">The secondary text (a hint, a relative path).</param>
    /// <param name="value">The value after <c>@kind:</c> (a full path, a symbol, an option); empty for a kind.</param>
    public MentionPickerItem(MentionPickerItemKind itemKind, string mentionKind, string label, string detail, string value)
        : this(itemKind, mentionKind, label, detail, value, MentionPickerViewModel.GroupOf(itemKind, mentionKind))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MentionPickerItem"/> class with a group.</summary>
    /// <param name="itemKind">What the entry is.</param>
    /// <param name="mentionKind">The mention kind it pins.</param>
    /// <param name="label">The text shown.</param>
    /// <param name="detail">The secondary text.</param>
    /// <param name="value">The value after <c>@kind:</c>; empty for a kind.</param>
    /// <param name="group">The heading of the group the entry is listed under.</param>
    public MentionPickerItem(MentionPickerItemKind itemKind, string mentionKind, string label, string detail, string value, string group)
    {
        ItemKind = itemKind;
        MentionKind = mentionKind;
        Label = label;
        Detail = detail;
        Value = value ?? string.Empty;
        Group = group ?? string.Empty;
    }

    /// <summary>Gets what the entry is.</summary>
    public MentionPickerItemKind ItemKind { get; }

    /// <summary>Gets the mention kind the entry pins; the window picks the entry's icon by it.</summary>
    public string MentionKind { get; }

    /// <summary>Gets the text shown.</summary>
    public string Label { get; }

    /// <summary>Gets the secondary text: the kind's hint line, a relative path, a symbol's container and location.</summary>
    public string Detail { get; }

    /// <summary>Gets the value after <c>@kind:</c>: a full path, a symbol, an option, or empty for a kind.</summary>
    public string Value { get; }

    /// <summary>Gets the heading of the group the entry is listed under (Kinds, Files, Symbols, ...).</summary>
    public string Group { get; }

    /// <summary>Gets a value indicating whether the entry starts its group (the window shows the heading above it).</summary>
    public bool IsFirstInGroup
    {
        get => _isFirstInGroup;
        internal set
        {
            if (SetProperty(ref _isFirstInGroup, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets the name a screen reader announces: the group (on its first entry), the label and the detail.</summary>
    public string AutomationName => (IsFirstInGroup && Group.Length > 0 ? Group + ": " : string.Empty) + (Detail.Length == 0 ? Label : Label + ", " + Detail);

    /// <summary>Gets or sets a value indicating whether the entry is the highlighted one.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Gets the mention text resolved for this entry, for example <c>@errors</c>, <c>@git:staged</c> or <c>@file:C:\src\Program.cs</c>.</summary>
    public string MentionText => ItemKind == MentionPickerItemKind.Kind || Value.Length == 0 ? "@" + MentionKind : "@" + MentionKind + ":" + Value;

    /// <summary>Gets the resolution the host already returned for this entry (a unique match while typing), so picking it pins at once.</summary>
    internal MentionResolution? Prefetched { get; set; }

    /// <summary>Gets a value indicating whether the entry came from the host (it is replaced when new host results arrive).</summary>
    internal bool IsFromHost { get; set; }

    /// <inheritdoc/>
    public override string ToString() => AutomationName;
}

/// <summary>
/// The mention picker shown at the caret while an <c>@</c> mention is typed (design section 7: "the picker shows matches as
/// you type"). Entries are grouped by kind under a heading: the kinds, each with its hint line; the files and folders of the
/// solution list; the symbols and documents the host finds (added by <see cref="SetHostItems"/> while typing after
/// <c>@symbol:</c> or <c>@docs:</c>); and the options of <c>@git</c> and <c>@output</c>. It doubles as the sub-picker of an
/// ambiguous mention. Keyboard: Up and Down move, Enter and Tab pick, Esc closes. Call on the UI thread.
/// </summary>
public sealed class MentionPickerViewModel : ObservableObject
{
    private static readonly string[] DocExtensions = [".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".asciidoc"];

    private bool _isOpen;
    private int _selectedIndex = -1;
    private string _title = string.Empty;
    private bool _isCandidates;
    private MentionToken? _token;
    private int? _dismissedAt;

    /// <summary>Gets the entries, in group order.</summary>
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

    /// <summary>Gets a value indicating whether the mention being typed asks the host for matches (<c>@symbol:</c> or <c>@docs:</c> with two or more characters).</summary>
    public bool WantsHostItems => IsOpen && !IsCandidates && _token is { Parsed: { HasKindSeparator: true } parsed } && parsed.Kind is MentionSyntax.Symbol or MentionSyntax.Docs && parsed.Value.Length >= 2;

    /// <summary>The heading an entry is listed under.</summary>
    /// <param name="itemKind">What the entry is.</param>
    /// <param name="mentionKind">The mention kind it pins.</param>
    /// <returns>The heading.</returns>
    public static string GroupOf(MentionPickerItemKind itemKind, string mentionKind) => itemKind switch
    {
        MentionPickerItemKind.Kind => ChatStrings.MentionGroupKinds,
        MentionPickerItemKind.File => ChatStrings.MentionGroupFiles,
        MentionPickerItemKind.Folder => ChatStrings.MentionGroupFolders,
        MentionPickerItemKind.Symbol => ChatStrings.MentionGroupSymbols,
        MentionPickerItemKind.Doc => ChatStrings.MentionGroupDocs,
        MentionPickerItemKind.Option => mentionKind switch
        {
            MentionSyntax.Git => ChatStrings.MentionGroupGit,
            MentionSyntax.Output => ChatStrings.MentionGroupOutput,
            MentionSyntax.Symbol => ChatStrings.MentionGroupSymbols,
            _ => ChatStrings.MentionGroupDocs,
        },
        _ => mentionKind switch
        {
            MentionSyntax.Symbol => ChatStrings.MentionGroupSymbols,
            MentionSyntax.Docs => ChatStrings.MentionGroupDocs,
            MentionSyntax.Folder => ChatStrings.MentionGroupFolders,
            _ => ChatStrings.MentionGroupFiles,
        },
    };

    /// <summary>The hint line of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The hint.</returns>
    public static string HintOf(string kind) => kind switch
    {
        MentionSyntax.File => ChatStrings.MentionKindFile,
        MentionSyntax.Folder => ChatStrings.MentionKindFolder,
        MentionSyntax.Selection => ChatStrings.MentionKindSelection,
        MentionSyntax.Errors => ChatStrings.MentionKindErrors,
        MentionSyntax.Clipboard => ChatStrings.MentionKindClipboard,
        MentionSyntax.Symbol => ChatStrings.MentionKindSymbol,
        MentionSyntax.Docs => ChatStrings.MentionKindDocs,
        MentionSyntax.Solution => ChatStrings.MentionKindSolution,
        MentionSyntax.Git => ChatStrings.MentionKindGit,
        MentionSyntax.Output => ChatStrings.MentionKindOutput,
        MentionSyntax.Build => ChatStrings.MentionKindBuild,
        MentionSyntax.Tests => ChatStrings.MentionKindTests,
        _ => string.Empty,
    };

    /// <summary>Determines whether a path is documentation (Markdown or text) by its extension.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see langword="true"/> for documentation.</returns>
    public static bool IsDoc(string path) => DocExtensions.Contains(Path.GetExtension(path ?? string.Empty), StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes a symbol entry from the host's <c>Dotted.Name#path:line</c> value.</summary>
    /// <param name="itemKind">Candidate or Symbol.</param>
    /// <param name="value">The value.</param>
    /// <param name="roots">The workspace roots, for the relative path.</param>
    /// <param name="prefetched">The resolution, when the host already resolved it.</param>
    /// <returns>The entry: the simple name as label, the dotted name and the location as detail.</returns>
    public static MentionPickerItem SymbolItem(MentionPickerItemKind itemKind, string value, IReadOnlyList<string> roots, MentionResolution? prefetched = null)
    {
        value ??= string.Empty;
        var hash = value.IndexOf('#');
        var name = hash >= 0 ? value.Substring(0, hash) : value;
        var location = hash >= 0 ? value.Substring(hash + 1) : string.Empty;
        var colon = location.LastIndexOf(':');
        var path = colon > 1 && int.TryParse(location.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? location.Substring(0, colon) : location;
        var line = location.Substring(path.Length);
        var simple = name.Substring(name.LastIndexOf('.') + 1);
        var detail = name + (path.Length == 0 ? string.Empty : ", " + MentionFileMatcher.Relative(path, roots ?? []) + line);
        return new MentionPickerItem(itemKind, MentionSyntax.Symbol, simple, detail, value, ChatStrings.MentionGroupSymbols)
        {
            Prefetched = prefetched,
            IsFromHost = itemKind == MentionPickerItemKind.Symbol,
        };
    }

    /// <summary>Makes a document entry the host found (a full path).</summary>
    /// <param name="path">The full path.</param>
    /// <param name="roots">The workspace roots, for the relative path.</param>
    /// <param name="prefetched">The resolution, when the host already resolved it.</param>
    /// <returns>The entry.</returns>
    public static MentionPickerItem DocItem(string path, IReadOnlyList<string> roots, MentionResolution? prefetched = null) =>
        new(MentionPickerItemKind.Doc, MentionSyntax.Docs, Path.GetFileName(path ?? string.Empty), MentionFileMatcher.Relative(path ?? string.Empty, roots ?? []), path ?? string.Empty, ChatStrings.MentionGroupDocs)
        {
            Prefetched = prefetched,
            IsFromHost = true,
        };

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
        var previous = _token;
        _token = token;
        IsCandidates = false;
        var parsed = token.Parsed;
        var items = new List<MentionPickerItem>();
        var list = files ?? WorkspaceFileList.Empty;
        string hint;
        if (!parsed.HasKindSeparator)
        {
            var typed = token.Query.ToLowerInvariant();
            items.AddRange(MentionSyntax.Kinds.Where(k => k.StartsWith(typed, StringComparison.Ordinal)).Select(KindItem));
            items.AddRange(MentionFileMatcher.MatchFiles(list.Files, list.Roots, token.Query).Select(m => PathItem(MentionPickerItemKind.File, MentionSyntax.File, m)));
            hint = token.Query.Length == 0 ? ChatStrings.MentionPickerHint : ChatStrings.MentionPickerMatchesTitle;
        }
        else
        {
            switch (parsed.Kind)
            {
                case MentionSyntax.File:
                    items.AddRange(MentionFileMatcher.MatchFiles(list.Files, list.Roots, parsed.Value).Select(m => PathItem(MentionPickerItemKind.File, MentionSyntax.File, m)));
                    hint = ChatStrings.MentionPickerFileHint;
                    break;
                case MentionSyntax.Folder:
                    items.AddRange(MentionFileMatcher.MatchFolders(list.Files, list.Roots, parsed.Value).Select(m => PathItem(MentionPickerItemKind.Folder, MentionSyntax.Folder, m)));
                    hint = ChatStrings.MentionPickerFolderHint;
                    break;
                case MentionSyntax.Symbol:
                    if (parsed.Value.Length > 0)
                    {
                        items.Add(TypedItem(MentionSyntax.Symbol, parsed.Value));
                    }

                    // Keep the host's symbols of the previous keystroke until new ones arrive, when they still match.
                    items.AddRange(KeptHostItems(previous, parsed));
                    hint = ChatStrings.MentionPickerSymbolHint;
                    break;
                case MentionSyntax.Docs:
                    var docs = MentionFileMatcher.MatchFiles(list.Files.Where(IsDoc).ToList(), list.Roots, parsed.Value)
                        .Select(m => PathItem(MentionPickerItemKind.Doc, MentionSyntax.Docs, m)).ToList();
                    items.AddRange(docs);
                    if (parsed.Value.Length > 0 && !docs.Any(d => string.Equals(Path.GetFileNameWithoutExtension(d.Value), parsed.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        items.Add(TypedItem(MentionSyntax.Docs, parsed.Value));
                    }

                    items.AddRange(KeptHostItems(previous, parsed).Where(h => !items.Any(i => string.Equals(i.Value, h.Value, StringComparison.OrdinalIgnoreCase))));
                    hint = ChatStrings.MentionPickerDocsHint;
                    break;
                case MentionSyntax.Git:
                    items.AddRange(GitOptions(parsed.Value));
                    hint = ChatStrings.MentionPickerGitHint;
                    break;
                case MentionSyntax.Output:
                    items.AddRange(OutputOptions(parsed.Value));
                    hint = ChatStrings.MentionPickerOutputHint;
                    break;
                default:
                    items.Add(KindItem(parsed.Kind));
                    hint = ChatStrings.MentionPickerMatchesTitle;
                    break;
            }

            if (parsed.Value.Length > 0)
            {
                hint = ChatStrings.MentionPickerMatchesTitle;
            }
        }

        Title = hint;
        if (items.Count == 0 && token.Query.Length > 0 && !(parsed.HasKindSeparator && parsed.Value.Length == 0))
        {
            Title = ChatStrings.MentionPickerNoMatches;
        }

        Fill(items, keep: null);
        IsOpen = true;
    }

    /// <summary>
    /// Adds the host's matches for the mention being typed (symbols or documents), replacing earlier host entries; ignored
    /// when the input moved on to another mention or value since they were asked for.
    /// </summary>
    /// <param name="token">The mention they were asked for.</param>
    /// <param name="items">The entries.</param>
    /// <returns><see langword="true"/> when they were shown.</returns>
    public bool SetHostItems(MentionToken token, IReadOnlyList<MentionPickerItem> items)
    {
        if (token is null || _token is null || !IsOpen || IsCandidates || _token.Start != token.Start || !string.Equals(_token.Query, token.Query, StringComparison.Ordinal))
        {
            return false;
        }

        items ??= [];
        var keep = SelectedItem;
        var merged = Items.Where(i => !i.IsFromHost).ToList();

        // A typed value the host resolved outright is listed by the host's entry instead.
        if (items.Any(h => h.Prefetched is not null))
        {
            merged.RemoveAll(i => i.ItemKind == MentionPickerItemKind.Option && i.MentionKind is MentionSyntax.Symbol or MentionSyntax.Docs);
        }

        foreach (var item in items)
        {
            if (!merged.Any(i => i.MentionKind == item.MentionKind && string.Equals(i.Value, item.Value, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(item);
            }
        }

        Fill(merged, keep);
        if (Items.Count > 0 && Title == ChatStrings.MentionPickerNoMatches)
        {
            Title = ChatStrings.MentionPickerMatchesTitle;
        }

        return true;
    }

    /// <summary>Opens the sub-picker with the matches an ambiguous mention offered.</summary>
    /// <param name="kind">The kind the mention resolved as (<c>file</c>, <c>folder</c>, <c>symbol</c> or <c>docs</c>).</param>
    /// <param name="mention">The mention that was ambiguous, for the title.</param>
    /// <param name="candidates">The full paths, or for <c>symbol</c> the host's <c>Name#path:line</c> values.</param>
    /// <param name="roots">The workspace roots, for relative paths.</param>
    public void ShowCandidates(string kind, string mention, IReadOnlyList<string> candidates, IReadOnlyList<string> roots)
    {
        _token = null;
        _dismissedAt = null;
        IsCandidates = true;
        var mentionKind = kind is MentionSyntax.Folder or MentionSyntax.Symbol or MentionSyntax.Docs ? kind : MentionSyntax.File;
        Title = ChatStrings.Format(mentionKind == MentionSyntax.Symbol ? ChatStrings.MentionSymbolCandidatesTitleFormat : ChatStrings.MentionCandidatesTitleFormat, mention);
        Fill(
            candidates.Select(c => mentionKind == MentionSyntax.Symbol
                ? SymbolItem(MentionPickerItemKind.Candidate, c, roots)
                : PathItem(MentionPickerItemKind.Candidate, mentionKind, new PathMatch(c, MentionFileMatcher.Relative(c, roots), 0))),
            keep: null);
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

    private static MentionPickerItem KindItem(string kind) => new(MentionPickerItemKind.Kind, kind, "@" + kind, HintOf(kind), string.Empty);

    private static MentionPickerItem PathItem(MentionPickerItemKind itemKind, string mentionKind, PathMatch match) =>
        new(itemKind, mentionKind, match.Name, match.RelativePath, match.FullPath);

    private static MentionPickerItem TypedItem(string kind, string value) =>
        new(MentionPickerItemKind.Option, kind, value, ChatStrings.Format(ChatStrings.MentionTypedValueFormat, value), value, GroupOf(MentionPickerItemKind.Option, kind));

    private static MentionPickerItem Option(string kind, string value, string detail) =>
        new(MentionPickerItemKind.Option, kind, value.Length == 0 ? "@" + kind : "@" + kind + ":" + value, detail, value);

    private static List<MentionPickerItem> GitOptions(string value)
    {
        var typed = value.Trim();

        // "log:5" or "log 5" names a count.
        var parts = typed.Split(MentionSyntax.OptionSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && string.Equals(parts[0], "log", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            return [Option(MentionSyntax.Git, "log:" + count.ToString(CultureInfo.InvariantCulture), ChatStrings.Format(ChatStrings.MentionGitLogFormat, count))];
        }

        var options = new List<MentionPickerItem>
        {
            Option(MentionSyntax.Git, string.Empty, ChatStrings.MentionGitDiff),
            Option(MentionSyntax.Git, "staged", ChatStrings.MentionGitStaged),
            Option(MentionSyntax.Git, "log", ChatStrings.Format(ChatStrings.MentionGitLogFormat, 10)),
        };
        if (typed.Length == 0)
        {
            return options;
        }

        var matching = options.Where(o => o.Value.Length > 0 && o.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 ? matching : [Option(MentionSyntax.Git, typed, ChatStrings.Format(ChatStrings.MentionTypedValueFormat, typed))];
    }

    private static IEnumerable<MentionPickerItem> OutputOptions(string value)
    {
        var options = new[]
        {
            Option(MentionSyntax.Output, "build", ChatStrings.MentionOutputBuild),
            Option(MentionSyntax.Output, "debug", ChatStrings.MentionOutputDebug),
            Option(MentionSyntax.Output, "tests", ChatStrings.MentionOutputTests),
        };
        var typed = value.Trim();
        return typed.Length == 0 ? options : options.Where(o => o.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
    }

    private List<MentionPickerItem> KeptHostItems(MentionToken? previous, ParsedMention parsed)
    {
        if (previous is null || _token is null || previous.Start != _token.Start || previous.Parsed.Kind != parsed.Kind)
        {
            return [];
        }

        return [.. Items.Where(i => i.IsFromHost && i.Prefetched is null
            && (i.Label.IndexOf(parsed.Value, StringComparison.OrdinalIgnoreCase) >= 0 || i.Value.IndexOf(parsed.Value, StringComparison.OrdinalIgnoreCase) >= 0))];
    }

    private void Fill(IEnumerable<MentionPickerItem> items, MentionPickerItem? keep)
    {
        // Groups keep the order in which they first appear; entries keep their order within a group.
        var list = items.ToList();
        var groups = list.Select(i => i.Group).Distinct(StringComparer.Ordinal).ToList();
        var ordered = list.Select((item, index) => (item, index)).OrderBy(p => groups.IndexOf(p.item.Group)).ThenBy(p => p.index).Select(p => p.item).ToList();
        Items.Clear();
        string? group = null;
        foreach (var item in ordered)
        {
            item.IsFirstInGroup = !string.Equals(item.Group, group, StringComparison.Ordinal);
            group = item.Group;
            item.IsSelected = false;
            Items.Add(item);
        }

        _selectedIndex = -1;
        var index = keep is null ? -1 : Items.IndexOf(keep);
        SelectedIndex = index >= 0 ? index : Items.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedItem));
    }
}
