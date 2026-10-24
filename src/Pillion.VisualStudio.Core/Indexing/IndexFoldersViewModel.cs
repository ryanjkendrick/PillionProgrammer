using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One index folder on this machine, as a row of the Indexes tab.</summary>
public sealed class IndexFolderRowViewModel : ObservableObject
{
    private readonly IndexFoldersViewModel _owner;

    internal IndexFolderRowViewModel(IndexFoldersViewModel owner, IndexInfo info)
    {
        _owner = owner;
        Info = info;
        RootText = string.IsNullOrWhiteSpace(info.RootPath) ? IndexStrings.UnknownRoot : info.RootPath!;
        DiskText = IndexFormat.Bytes(info.DiskBytes);
        ModelText = string.IsNullOrWhiteSpace(info.EmbeddingModelId) ? IndexStrings.Unknown : info.EmbeddingModelId!;
        LastUsedText = IndexFormat.Moment(info.LastUsedUtc == default ? null : info.LastUsedUtc);
        IsStale = info.RootExists == false;
        RootExistsText = info.RootExists switch
        {
            true => IndexStrings.RootExists,
            false => IndexStrings.RootGone,
            _ => IndexStrings.RootUnknown,
        };
        InUseText = info.IsOpen
            ? IndexStrings.InUseOpen
            : info.IsInUse ? IndexStrings.InUseOtherHost : IndexStrings.NotInUse;
        AutomationName = IndexStrings.Format(IndexStrings.IndexRowNameFormat, RootText, DiskText, RootExistsText, InUseText);
        DeleteName = IndexStrings.Format(IndexStrings.DeleteIndexNameFormat, RootText);
        DeleteCommand = new DelegateCommand(() => _owner.Owner.RunUserAction(() => _owner.DeleteAsync(this)), () => CanDelete && _owner.Owner.CanAct);
    }

    /// <summary>Gets the host's entry.</summary>
    public IndexInfo Info { get; }

    /// <summary>Gets the index id (the folder name).</summary>
    public string IndexId => Info.IndexId;

    /// <summary>Gets the repository root the index covers, or "unknown root" when its state file is missing.</summary>
    public string RootText { get; }

    /// <summary>Gets the folder's disk use.</summary>
    public string DiskText { get; }

    /// <summary>Gets the embedding model id, or "unknown".</summary>
    public string ModelText { get; }

    /// <summary>Gets when the index was last used.</summary>
    public string LastUsedText { get; }

    /// <summary>Gets a value indicating whether the repository root no longer exists: the retention sweep deletes such indexes.</summary>
    public bool IsStale { get; }

    /// <summary>Gets whether the root still exists: yes, gone (stale), or unknown (the drive is offline or there is no state file).</summary>
    public string RootExistsText { get; }

    /// <summary>Gets whether the index is open here, held by another running host, or not in use.</summary>
    public string InUseText { get; }

    /// <summary>Gets a value indicating whether this host has the index open for the current solution or folder.</summary>
    public bool IsOpen => Info.IsOpen;

    /// <summary>
    /// Gets a value indicating whether Delete is offered: not while another live host owns the index (the host refuses it).
    /// An index this host has open is closed first.
    /// </summary>
    public bool CanDelete => !Info.IsInUse || Info.IsOpen;

    /// <summary>Gets the name a screen reader announces for the row.</summary>
    public string AutomationName { get; }

    /// <summary>Gets the screen-reader name of the Delete button, which names the root.</summary>
    public string DeleteName { get; }

    /// <summary>Gets the Delete command (asks first).</summary>
    public DelegateCommand DeleteCommand { get; }
}

/// <summary>
/// The Indexes tab of the Index window: every index folder on this machine with its disk use, whether its root still
/// exists and whether it is in use, and Delete (<see cref="IContextService.ListIndexesAsync"/>, retention: design section
/// 10). Read when the window opens, after a delete, when the set of indexes the status reports changes, and on demand.
/// Members are used on the UI thread.
/// </summary>
public sealed class IndexFoldersViewModel : ObservableObject
{
    private readonly IndexServiceCaller _caller;
    private bool _isLoading;
    private string _message = string.Empty;
    private string _summary = IndexStrings.IndexesNone;
    private int _loadVersion;

    internal IndexFoldersViewModel(IndexWindowViewModel owner, IndexServiceCaller caller)
    {
        Owner = owner;
        _caller = caller;
        RefreshCommand = new DelegateCommand(() => Owner.RunBackground(LoadAsync, "indexes"), () => !_isLoading);
    }

    /// <summary>Gets the index folders, ordered by root path.</summary>
    public ObservableCollection<IndexFolderRowViewModel> Items { get; } = [];

    /// <summary>Gets a value indicating whether the list is being read.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the line above the list, for example "3 indexes, 5.1 MB on disk (1 stale)".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
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

    /// <summary>Gets the command that reads the list again.</summary>
    public DelegateCommand RefreshCommand { get; }

    internal IndexWindowViewModel Owner { get; }

    /// <summary>Reads the index folders from the host and shows them.</summary>
    /// <returns>A task that completes when the list (or the reason it could not be read) is shown. Call on the UI thread.</returns>
    public async Task LoadAsync()
    {
        if (!_caller.IsHostReady)
        {
            Message = IndexStrings.HostNotReady;
            return;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        IsLoading = true;
        var result = await _caller.CallAsync("ListIndexesAsync", (service, token) => service.ListIndexesAsync(token)).ConfigureAwait(false);

        await _caller.JoinableTaskFactory.SwitchToMainThreadAsync(_caller.Disposal);
        if (version != Volatile.Read(ref _loadVersion))
        {
            return; // A newer read is on its way.
        }

        IsLoading = false;
        if (!result.Succeeded || result.Value is null)
        {
            Message = IndexStrings.Format(IndexStrings.IndexesFailedFormat, result.Failure?.Message ?? IndexStrings.Unknown);
            return;
        }

        Message = string.Empty;
        Items.Clear();
        foreach (var info in result.Value.Where(i => i is not null))
        {
            Items.Add(new IndexFolderRowViewModel(this, info));
        }

        var stale = Items.Count(i => i.IsStale);
        var bytes = Items.Sum(i => i.Info.DiskBytes);
        Summary = Items.Count == 0
            ? IndexStrings.IndexesNone
            : IndexStrings.Format(IndexStrings.IndexesSummaryFormat, IndexFormat.Count(Items.Count), IndexFormat.Bytes(bytes), IndexFormat.Count(stale));
        RaiseCommands();
    }

    /// <summary>Deletes an index folder after the window's confirmation, then reads the list again.</summary>
    /// <param name="row">The row.</param>
    /// <returns>A task that completes when the delete (or the refusal) is reported. Call on the UI thread.</returns>
    public async Task DeleteAsync(IndexFolderRowViewModel row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        var question = row.IsOpen
            ? IndexStrings.Format(IndexStrings.DeleteOpenIndexConfirmFormat, row.RootText, row.DiskText)
            : IndexStrings.Format(IndexStrings.DeleteIndexConfirmFormat, row.RootText, row.DiskText);
        if (!Owner.Confirm(question))
        {
            return;
        }

        await Owner.DeleteIndexFolderAsync(row.IndexId, row.RootText).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    internal void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        foreach (var row in Items)
        {
            row.DeleteCommand.RaiseCanExecuteChanged();
        }
    }
}
