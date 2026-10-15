using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One file or folder the index does not cover, as a row of the Excluded files list.</summary>
public sealed class ExcludedFileRowViewModel
{
    internal ExcludedFileRowViewModel(string rootPath, ExcludedFile file)
    {
        File = file;
        RelativePath = file.Path ?? string.Empty;
        FullPath = CombineRoot(rootPath, RelativePath);
        ReasonText = IndexFormat.ExcludedReason(file.Reason);
        Description = file.Description ?? string.Empty;
        IsSecretBearing = file.IsSecretBearing;
        IsDirectory = file.IsDirectory;
        GroupName = IsSecretBearing ? IndexStrings.GroupSecretBearing : IndexStrings.GroupOtherExclusions;
        KindText = IsSecretBearing
            ? (IsDirectory ? IndexStrings.KindSecretFolder : IndexStrings.KindSecretFile)
            : (IsDirectory ? IndexStrings.KindFolder : IndexStrings.KindFile);
        AutomationName = IndexStrings.Format(IndexStrings.ExcludedRowNameFormat, RelativePath, KindText, ReasonText, Description);
    }

    /// <summary>Gets the host's entry.</summary>
    public ExcludedFile File { get; }

    /// <summary>Gets the path relative to the root, with <c>/</c> separators.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the path on disk (the root and the relative path), which Copy path puts on the clipboard.</summary>
    public string FullPath { get; }

    /// <summary>Gets the rule that excluded it, as a name.</summary>
    public string ReasonText { get; }

    /// <summary>Gets the sentence naming the exact rule (the gitignore line, the pattern, the size).</summary>
    public string Description { get; }

    /// <summary>Gets a value indicating whether the file is secret-bearing (marked with the shield glyph and grouped first).</summary>
    public bool IsSecretBearing { get; }

    /// <summary>Gets a value indicating whether the entry is a folder excluded as a whole.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the group the list shows it in: secret-bearing first, then the rest.</summary>
    public string GroupName { get; }

    /// <summary>Gets what the entry is, as words, so the shield is never the only sign: "Secret-bearing file", "File", "Folder".</summary>
    public string KindText { get; }

    /// <summary>Gets the name a screen reader announces for the row.</summary>
    public string AutomationName { get; }

    /// <inheritdoc/>
    /// <remarks>The list item's screen-reader name: Windows takes it from this text.</remarks>
    public override string ToString() => AutomationName;

    private static string CombineRoot(string rootPath, string relative)
    {
        if (string.IsNullOrEmpty(rootPath))
        {
            return relative;
        }

        try
        {
            return Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException)
        {
            return relative;
        }
    }
}

/// <summary>
/// The Excluded files tab of the Index window: for one root, the files and folders the index does not cover and the rule
/// that excluded each, one page at a time (<see cref="IContextService.ListExcludedFilesAsync"/>), the secret-bearing ones
/// first and marked. Loaded when a root is chosen and on demand; the host's status notifications reload it only when the
/// chosen root's exclusion counts change. Members are used on the UI thread.
/// </summary>
public sealed class ExcludedFilesViewModel : ObservableObject
{
    private readonly IndexWindowViewModel _owner;
    private readonly IndexServiceCaller _caller;
    private IndexRootRowViewModel? _selectedRoot;
    private ExcludedFileRowViewModel? _selectedItem;
    private bool _secretOnly;
    private int _pageIndex;
    private int _totalCount;
    private bool _hasMore;
    private bool _isLoading;
    private string _message = string.Empty;
    private int _loadVersion;
    private int _loadedExcluded = -1;
    private int _loadedSecret = -1;

    internal ExcludedFilesViewModel(IndexWindowViewModel owner, IndexServiceCaller caller, int pageSize)
    {
        _owner = owner;
        _caller = caller;
        PageSize = Math.Max(1, pageSize);
        PreviousPageCommand = new DelegateCommand(() => RunLoad(_pageIndex - 1), () => _pageIndex > 0 && !_isLoading);
        NextPageCommand = new DelegateCommand(() => RunLoad(_pageIndex + 1), () => _hasMore && !_isLoading);
        RefreshCommand = new DelegateCommand(() => RunLoad(_pageIndex), () => _selectedRoot is not null && !_isLoading);
        CopyPathCommand = new DelegateCommand(CopyPath, () => _selectedItem is not null && _owner.CanCopy);
        OpenPillionIgnoreCommand = new DelegateCommand(OpenPillionIgnore, () => _selectedRoot is not null && _owner.CanOpenPillionIgnore);
    }

    /// <summary>Gets the entries of the page being shown, secret-bearing ones first.</summary>
    public ObservableCollection<ExcludedFileRowViewModel> Items { get; } = [];

    /// <summary>Gets how many entries one page holds.</summary>
    public int PageSize { get; }

    /// <summary>Gets or sets the root whose exclusions are listed; choosing one loads its first page.</summary>
    public IndexRootRowViewModel? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (SetProperty(ref _selectedRoot, value))
            {
                _loadedExcluded = -1;
                _loadedSecret = -1;
                RunLoad(0);
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets or sets the chosen entry (Copy path acts on it).</summary>
    public ExcludedFileRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                CopyPathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether only the secret-bearing files are listed.</summary>
    public bool SecretOnly
    {
        get => _secretOnly;
        set
        {
            if (SetProperty(ref _secretOnly, value))
            {
                RunLoad(0);
            }
        }
    }

    /// <summary>Gets the zero-based index of the page being shown.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set => SetProperty(ref _pageIndex, value);
    }

    /// <summary>Gets how many entries match, over every page.</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    /// <summary>Gets a value indicating whether more entries follow this page.</summary>
    public bool HasMore
    {
        get => _hasMore;
        private set => SetProperty(ref _hasMore, value);
    }

    /// <summary>Gets a value indicating whether a page is being read.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets which entries the page shows, for example "Showing 1-200 of 1,432", or why there are none.</summary>
    public string PageText
    {
        get
        {
            if (_selectedRoot is null)
            {
                return IndexStrings.ExcludedNoRoot;
            }

            if (_totalCount == 0)
            {
                return _secretOnly ? IndexStrings.ExcludedNoSecretFiles : IndexStrings.ExcludedNone;
            }

            var first = (_pageIndex * PageSize) + 1;
            var last = Math.Min(_totalCount, first + Items.Count - 1);
            return IndexStrings.Format(IndexStrings.ExcludedPageFormat, IndexFormat.Count(first), IndexFormat.Count(last), IndexFormat.Count(_totalCount));
        }
    }

    /// <summary>Gets why the list could not be read, or empty.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Message"/> has text.</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>Gets the command that shows the previous page.</summary>
    public DelegateCommand PreviousPageCommand { get; }

    /// <summary>Gets the command that shows the next page.</summary>
    public DelegateCommand NextPageCommand { get; }

    /// <summary>Gets the command that reads the current page again.</summary>
    public DelegateCommand RefreshCommand { get; }

    /// <summary>Gets the command that copies the chosen entry's path to the clipboard.</summary>
    public DelegateCommand CopyPathCommand { get; }

    /// <summary>Gets the command that opens the chosen root's <c>.pillionignore</c> for editing.</summary>
    public DelegateCommand OpenPillionIgnoreCommand { get; }

    /// <summary>Reads a page of the chosen root's exclusions and shows it.</summary>
    /// <param name="pageIndex">The zero-based page; clamped at zero.</param>
    /// <returns>A task that completes when the page (or the reason it could not be read) is shown. Call on the UI thread.</returns>
    public async Task LoadAsync(int pageIndex = 0)
    {
        var root = _selectedRoot;
        var version = Interlocked.Increment(ref _loadVersion);
        if (root is null)
        {
            Items.Clear();
            SelectedItem = null;
            TotalCount = 0;
            HasMore = false;
            PageIndex = 0;
            Message = string.Empty;
            IsLoading = false;
            OnPropertyChanged(nameof(PageText));
            return;
        }

        if (!_caller.IsHostReady)
        {
            Message = IndexStrings.HostNotReady;
            return;
        }

        pageIndex = Math.Max(0, pageIndex);
        IsLoading = true;
        var request = new ExcludedFilesRequest
        {
            RootPath = root.RootPath,
            Offset = pageIndex * PageSize,
            Limit = PageSize,
            SecretOnly = _secretOnly,
        };
        var result = await _caller.CallAsync("ListExcludedFilesAsync", (service, token) => service.ListExcludedFilesAsync(request, token)).ConfigureAwait(false);

        await _caller.JoinableTaskFactory.SwitchToMainThreadAsync(_caller.Disposal);
        if (version != Volatile.Read(ref _loadVersion))
        {
            return; // A newer read is on its way.
        }

        IsLoading = false;
        if (!result.Succeeded || result.Value is null)
        {
            Message = IndexStrings.Format(IndexStrings.ExcludedFailedFormat, result.Failure?.Message ?? IndexStrings.Unknown);
            OnPropertyChanged(nameof(PageText));
            return;
        }

        Message = string.Empty;
        var page = result.Value;
        _loadedExcluded = root.ExcludedFileCount;
        _loadedSecret = root.SecretFileCount;
        Items.Clear();
        foreach (var item in page.Items ?? [])
        {
            if (item is not null)
            {
                Items.Add(new ExcludedFileRowViewModel(root.RootPath, item));
            }
        }

        SelectedItem = null;
        PageIndex = pageIndex;
        TotalCount = page.TotalCount;
        HasMore = page.HasMore;
        OnPropertyChanged(nameof(PageText));
        RaiseCommands();
    }

    /// <summary>
    /// Called after the window applied a new status: keeps the chosen root pointing at a live row (a rebuild or a deleted
    /// index replaces rows), chooses the first root when none is chosen, and reads the list again when the chosen root's
    /// exclusion counts changed since it was read.
    /// </summary>
    internal void OnRootsChanged()
    {
        var roots = _owner.Roots;
        if (_selectedRoot is { } current)
        {
            var same = FindRow(roots, current.RootPath);
            if (same is null)
            {
                SelectedRoot = roots.Count > 0 ? roots[0] : null;
                return;
            }

            if (!ReferenceEquals(same, current))
            {
                _selectedRoot = same;
                OnPropertyChanged(nameof(SelectedRoot));
            }

            if (_loadedExcluded >= 0 && (same.ExcludedFileCount != _loadedExcluded || same.SecretFileCount != _loadedSecret) && !_isLoading)
            {
                RunLoad(_pageIndex);
            }
        }
        else if (roots.Count > 0)
        {
            SelectedRoot = roots[0];
        }

        RaiseCommands();
    }

    /// <summary>Reads the chosen root's first page again (after the host became ready).</summary>
    internal void Reload() => RunLoad(0);

    internal void RaiseCommands()
    {
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
        OpenPillionIgnoreCommand.RaiseCanExecuteChanged();
    }

    private static IndexRootRowViewModel? FindRow(ObservableCollection<IndexRootRowViewModel> roots, string rootPath)
    {
        foreach (var row in roots)
        {
            if (string.Equals(row.RootPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    private void RunLoad(int pageIndex) => _owner.RunBackground(() => LoadAsync(pageIndex), "excluded");

    private void CopyPath()
    {
        if (_selectedItem is { } item)
        {
            _owner.Copy(item.FullPath);
        }
    }

    private void OpenPillionIgnore()
    {
        if (_selectedRoot is { } root)
        {
            _owner.OpenPillionIgnore(root.RootPath);
        }
    }
}
