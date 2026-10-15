using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One repository root of the open solution in the Index window: its path, whether this host writes or reads the index,
/// the counts, disk use and embedding model, with Rebuild and Delete. Updated in place when the host reports a new status.
/// Members are used on the UI thread.
/// </summary>
public sealed class IndexRootRowViewModel : ObservableObject
{
    private readonly IndexWindowViewModel _owner;
    private IndexRootStatus _status;

    internal IndexRootRowViewModel(IndexWindowViewModel owner, IndexRootStatus status)
    {
        _owner = owner;
        _status = status;
        RebuildCommand = new DelegateCommand(() => _owner.RunUserAction(() => _owner.RebuildAsync(this)), () => _owner.CanRebuild(this));
        DeleteCommand = new DelegateCommand(() => _owner.RunUserAction(() => _owner.DeleteAsync(this)), () => _owner.CanDelete(this));
    }

    /// <summary>Gets the full path of the repository root. It identifies the row.</summary>
    public string RootPath => _status.RootPath;

    /// <summary>Gets the index id (the folder name under <c>index\</c>); empty until the index is opened.</summary>
    public string IndexId => _status.IndexId;

    /// <summary>Gets whether this host owns the index or reads it.</summary>
    public IndexRole Role => _status.Role;

    /// <summary>Gets the role as text: writer, reader or "not open".</summary>
    public string RoleText => IndexFormat.Role(_status.Role);

    /// <summary>Gets the files indexed out of the files that pass the exclusions, for example "40 of 40".</summary>
    public string FilesText => IndexStrings.Format(IndexStrings.FilesOfFormat, IndexFormat.Count(_status.IndexedFileCount), IndexFormat.Count(_status.TotalFileCount));

    /// <summary>Gets the number of chunks.</summary>
    public string ChunksText => IndexFormat.Count(_status.ChunkCount);

    /// <summary>Gets the index folder's disk use.</summary>
    public string DiskText => IndexFormat.Bytes(_status.DiskBytes);

    /// <summary>Gets the embedding model the index was built with, or "unknown" for an index not opened yet.</summary>
    public string ModelText => string.IsNullOrWhiteSpace(_status.EmbeddingModelId) ? IndexStrings.Unknown : _status.EmbeddingModelId;

    /// <summary>Gets the chunker and schema versions.</summary>
    public string VersionsText => IndexStrings.Format(IndexStrings.VersionsFormat, string.IsNullOrWhiteSpace(_status.ChunkerVersion) ? "-" : _status.ChunkerVersion, _status.SchemaVersion);

    /// <summary>Gets when the last full hash sweep finished.</summary>
    public string LastSweepText => IndexFormat.Moment(_status.LastFullSweepUtc);

    /// <summary>Gets how many files and folders are excluded, and how many of those are secret-bearing.</summary>
    public int ExcludedFileCount => _status.ExcludedFileCount;

    /// <summary>Gets how many excluded files are secret-bearing.</summary>
    public int SecretFileCount => _status.SecretFileCount;

    /// <summary>Gets the exclusions as text, for example "6 excluded, 2 secret-bearing".</summary>
    public string ExcludedText => IndexStrings.Format(IndexStrings.ExcludedCountFormat, IndexFormat.Count(_status.ExcludedFileCount), IndexFormat.Count(_status.SecretFileCount));

    /// <summary>Gets the name a screen reader announces for the row.</summary>
    public string AutomationName => IndexStrings.Format(
        IndexStrings.RootRowNameFormat,
        RootPath,
        RoleText,
        FilesText,
        ChunksText,
        DiskText,
        ModelText);

    /// <summary>Gets the screen-reader name of the Rebuild button, which names the root.</summary>
    public string RebuildName => IndexStrings.Format(IndexStrings.RebuildRootNameFormat, RootPath);

    /// <summary>Gets the screen-reader name of the Delete button, which names the root.</summary>
    public string DeleteName => IndexStrings.Format(IndexStrings.DeleteRootNameFormat, RootPath);

    /// <summary>Gets the Rebuild command (starts a rebuild of this root's index in the background).</summary>
    public DelegateCommand RebuildCommand { get; }

    /// <summary>Gets the Delete command (asks first, then deletes this root's index folder).</summary>
    public DelegateCommand DeleteCommand { get; }

    internal IndexRootStatus Status => _status;

    /// <summary>Takes the host's latest numbers for this root and raises change notifications for what differs.</summary>
    /// <param name="status">The new status of the same root.</param>
    internal void Update(IndexRootStatus status)
    {
        var previous = _status;
        _status = status;
        if (previous.IndexId != status.IndexId)
        {
            OnPropertyChanged(nameof(IndexId));
        }

        if (previous.Role != status.Role)
        {
            OnPropertyChanged(nameof(Role));
            OnPropertyChanged(nameof(RoleText));
        }

        if (previous.IndexedFileCount != status.IndexedFileCount || previous.TotalFileCount != status.TotalFileCount)
        {
            OnPropertyChanged(nameof(FilesText));
        }

        if (previous.ChunkCount != status.ChunkCount)
        {
            OnPropertyChanged(nameof(ChunksText));
        }

        if (previous.DiskBytes != status.DiskBytes)
        {
            OnPropertyChanged(nameof(DiskText));
        }

        if (previous.EmbeddingModelId != status.EmbeddingModelId)
        {
            OnPropertyChanged(nameof(ModelText));
        }

        if (previous.ChunkerVersion != status.ChunkerVersion || previous.SchemaVersion != status.SchemaVersion)
        {
            OnPropertyChanged(nameof(VersionsText));
        }

        if (previous.LastFullSweepUtc != status.LastFullSweepUtc)
        {
            OnPropertyChanged(nameof(LastSweepText));
        }

        if (previous.ExcludedFileCount != status.ExcludedFileCount || previous.SecretFileCount != status.SecretFileCount)
        {
            OnPropertyChanged(nameof(ExcludedFileCount));
            OnPropertyChanged(nameof(SecretFileCount));
            OnPropertyChanged(nameof(ExcludedText));
        }

        OnPropertyChanged(nameof(AutomationName));
    }

    internal void RaiseCommands()
    {
        RebuildCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc/>
    public override string ToString() => RootPath;
}
