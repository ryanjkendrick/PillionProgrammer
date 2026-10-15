using Pillion.Indexing.Discovery;
using Pillion.Indexing.Storage;

namespace Pillion.Indexing.Sweep;

/// <summary>
/// What a sweep found by comparing the files on disk with the stored <c>files</c> rows (design section 7: a branch
/// switch triggers this sweep, not a rebuild). The sets are disjoint and together cover every stored row and every
/// indexed file on disk. Nothing has been written: the pipeline stores <see cref="Added"/> and <see cref="Changed"/>
/// after chunking them, and <see cref="FilesRepository.CommitAsync"/> applies the lot at once.
/// </summary>
public sealed class SweepResult
{
    /// <summary>Gets files with no stored row: new files. Their hash, size and time are filled in; <see cref="FileRecord.Id"/> is 0.</summary>
    public required IReadOnlyList<FileRecord> Added { get; init; }

    /// <summary>Gets files whose content hash differs from the stored one. The record carries the stored id and the new hash, size and time.</summary>
    public required IReadOnlyList<FileRecord> Changed { get; init; }

    /// <summary>
    /// Gets files whose content is identical (same hash) but whose size, time, language, partition or path casing
    /// differs from the row, for example after a <c>touch</c> or a checkout that rewrote the same bytes. Their chunks
    /// stay valid; the record carries the refreshed metadata to store.
    /// </summary>
    public required IReadOnlyList<FileRecord> Touched { get; init; }

    /// <summary>Gets stored rows whose file has the same size and modified time, so it was not read or hashed.</summary>
    public required IReadOnlyList<FileRecord> Unchanged { get; init; }

    /// <summary>Gets stored rows with no indexed file on disk any more: removed, renamed, or newly excluded (ignored, too large, binary, secret).</summary>
    public required IReadOnlyList<FileRecord> Deleted { get; init; }

    /// <summary>Gets what was excluded, with the reason, secret-bearing files apart (see <see cref="SecretFiles"/>). Directories excluded as a whole appear once.</summary>
    public required IReadOnlyList<ScanEntry> Excluded { get; init; }

    /// <summary>Gets the secret-bearing files: never indexed, listed in the Index window, and readable by agent tools only with a per-file approval.</summary>
    public required IReadOnlyList<ScanEntry> SecretFiles { get; init; }

    /// <summary>Gets files that passed the rules but could not be read (locked, access denied). A stored row for such a file is kept, not deleted.</summary>
    public required IReadOnlyList<string> Unreadable { get; init; }

    /// <summary>Gets how many files were read and hashed in this sweep (added, changed and touched).</summary>
    public int FilesHashed => Added.Count + Changed.Count + Touched.Count;

    /// <summary>Gets how long the sweep took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets a value indicating whether anything needs to be stored or removed.</summary>
    public bool HasChanges => Added.Count + Changed.Count + Touched.Count + Deleted.Count > 0;
}
