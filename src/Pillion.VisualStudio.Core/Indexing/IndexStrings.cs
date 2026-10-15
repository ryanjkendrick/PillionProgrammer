using System.Globalization;
using System.Resources;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The Index window's, the status bar indicator's and the Indexing Options page's user-facing strings, from
/// <c>Indexing/IndexStrings.resx</c> (design doc section 5, Localisation). The XAML binds to these with <c>x:Static</c>;
/// translations go in satellite files such as <c>IndexStrings.de.resx</c>.
/// </summary>
public static class IndexStrings
{
    /// <summary>The manifest name of <c>Indexing/IndexStrings.resx</c>: the root namespace plus the folder.</summary>
    private const string ResourceName = "Pillion.VisualStudio.Core.Indexing.IndexStrings";

    private static readonly ResourceManager Resources = new(ResourceName, typeof(IndexStrings).Assembly);

    /// <summary>Gets the string: Caption of the Index tool window and its View menu entry.</summary>
    public static string WindowTitle => Get(nameof(WindowTitle));

    /// <summary>Gets the string: Screen-reader name of the Index window.</summary>
    public static string WindowName => Get(nameof(WindowName));

    /// <summary>Gets the string: Shown instead of the index while the indexWindow feature flag is off.</summary>
    public static string DisabledMessage => Get(nameof(DisabledMessage));

    /// <summary>Gets the string: Shown while Pillion Host is not connected.</summary>
    public static string HostNotReady => Get(nameof(HostNotReady));

    /// <summary>Gets the string: Phase line before the host has reported the index status.</summary>
    public static string NoStatusYet => Get(nameof(NoStatusYet));

    /// <summary>Gets the string: Shown when the status call failed; {0} is the reason.</summary>
    public static string StatusFailedFormat => Get(nameof(StatusFailedFormat));

    /// <summary>Gets the string: Screen-reader name of the status header group.</summary>
    public static string HeaderName => Get(nameof(HeaderName));

    /// <summary>Gets the string: Button that pauses indexing; the underscore marks the access key.</summary>
    public static string PauseButton => Get(nameof(PauseButton));

    /// <summary>Gets the string: Screen-reader name of the Pause button.</summary>
    public static string PauseName => Get(nameof(PauseName));

    /// <summary>Gets the string: Tooltip of the Pause button.</summary>
    public static string PauseToolTip => Get(nameof(PauseToolTip));

    /// <summary>Gets the string: Button that resumes indexing.</summary>
    public static string ResumeButton => Get(nameof(ResumeButton));

    /// <summary>Gets the string: Screen-reader name of the Resume button.</summary>
    public static string ResumeName => Get(nameof(ResumeName));

    /// <summary>Gets the string: Tooltip of the Resume button.</summary>
    public static string ResumeToolTip => Get(nameof(ResumeToolTip));

    /// <summary>Gets the string: Button that reads the index status again.</summary>
    public static string RefreshButton => Get(nameof(RefreshButton));

    /// <summary>Gets the string: Screen-reader name of the Refresh button.</summary>
    public static string RefreshName => Get(nameof(RefreshName));

    /// <summary>Gets the string: Tooltip of the Refresh button.</summary>
    public static string RefreshToolTip => Get(nameof(RefreshToolTip));

    /// <summary>Gets the string: Header of the tab that lists the repository roots.</summary>
    public static string TabRoots => Get(nameof(TabRoots));

    /// <summary>Gets the string: Screen-reader name of the Roots tab.</summary>
    public static string TabRootsName => Get(nameof(TabRootsName));

    /// <summary>Gets the string: Header of the tab that lists excluded files.</summary>
    public static string TabExcluded => Get(nameof(TabExcluded));

    /// <summary>Gets the string: Screen-reader name of the Excluded files tab.</summary>
    public static string TabExcludedName => Get(nameof(TabExcludedName));

    /// <summary>Gets the string: Header of the tab that lists every index folder.</summary>
    public static string TabIndexes => Get(nameof(TabIndexes));

    /// <summary>Gets the string: Screen-reader name of the Indexes tab.</summary>
    public static string TabIndexesName => Get(nameof(TabIndexesName));

    /// <summary>Gets the string: Last error the indexer met; {0} is the message.</summary>
    public static string LastErrorFormat => Get(nameof(LastErrorFormat));

    /// <summary>Gets the string: Screen-reader name of the progress bar.</summary>
    public static string ProgressName => Get(nameof(ProgressName));

    /// <summary>Gets the string: Progress line; {0} files done, {1} files in the run, {2} chunks stored.</summary>
    public static string ProgressFormat => Get(nameof(ProgressFormat));

    /// <summary>Gets the string: Line shown while indexing is paused for no known reason.</summary>
    public static string PausedLine => Get(nameof(PausedLine));

    /// <summary>Gets the string: Line shown while indexing is paused; {0} is the reason, for example a build is running.</summary>
    public static string PausedLineFormat => Get(nameof(PausedLineFormat));

    /// <summary>Gets the string: Totals line; files indexed, roots, chunks, disk use, secret-bearing files.</summary>
    public static string TotalsFormat => Get(nameof(TotalsFormat));

    /// <summary>Gets the string: Line with when the index was last updated; {0} is a date and time.</summary>
    public static string LastUpdatedFormat => Get(nameof(LastUpdatedFormat));

    /// <summary>Gets the string: Shown on the Roots tab while the host reports no root.</summary>
    public static string NoRoots => Get(nameof(NoRoots));

    /// <summary>Gets the string: Screen-reader name of the line that reports the last action.</summary>
    public static string ActionMessageName => Get(nameof(ActionMessageName));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseIdle => Get(nameof(PhaseIdle));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseDiscovering => Get(nameof(PhaseDiscovering));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseHashing => Get(nameof(PhaseHashing));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseChunking => Get(nameof(PhaseChunking));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseEmbedding => Get(nameof(PhaseEmbedding));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseStoring => Get(nameof(PhaseStoring));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhasePaused => Get(nameof(PhasePaused));

    /// <summary>Gets the string: Indexing phase.</summary>
    public static string PhaseFailed => Get(nameof(PhaseFailed));

    /// <summary>Gets the string: Why indexing is paused (long form, after 'Paused: ').</summary>
    public static string PauseUser => Get(nameof(PauseUser));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseBuild => Get(nameof(PauseBuild));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseTest => Get(nameof(PauseTest));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseDebug => Get(nameof(PauseDebug));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseBattery => Get(nameof(PauseBattery));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseMemory => Get(nameof(PauseMemory));

    /// <summary>Gets the string: Why indexing is paused (long form).</summary>
    public static string PauseLowDisk => Get(nameof(PauseLowDisk));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseUserShort => Get(nameof(PauseUserShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseBuildShort => Get(nameof(PauseBuildShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseTestShort => Get(nameof(PauseTestShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseDebugShort => Get(nameof(PauseDebugShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseBatteryShort => Get(nameof(PauseBatteryShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseMemoryShort => Get(nameof(PauseMemoryShort));

    /// <summary>Gets the string: Why indexing is paused (status bar).</summary>
    public static string PauseLowDiskShort => Get(nameof(PauseLowDiskShort));

    /// <summary>Gets the string: Shown when Pause failed; {0} is the reason.</summary>
    public static string PauseFailedFormat => Get(nameof(PauseFailedFormat));

    /// <summary>Gets the string: Shown when Resume failed; {0} is the reason.</summary>
    public static string ResumeFailedFormat => Get(nameof(ResumeFailedFormat));

    /// <summary>Gets the string: Estimated time left.</summary>
    public static string RemainingUnderMinute => Get(nameof(RemainingUnderMinute));

    /// <summary>Gets the string: Estimated time left; {0} is minutes.</summary>
    public static string RemainingMinutesFormat => Get(nameof(RemainingMinutesFormat));

    /// <summary>Gets the string: Estimated time left; {0} hours, {1} minutes.</summary>
    public static string RemainingHoursFormat => Get(nameof(RemainingHoursFormat));

    /// <summary>Gets the string: Shown when no full sweep has finished yet.</summary>
    public static string SweepNever => Get(nameof(SweepNever));

    /// <summary>Gets the string: Shown for a value the host does not know.</summary>
    public static string Unknown => Get(nameof(Unknown));

    /// <summary>Gets the string: Shown for an index folder whose state file is missing.</summary>
    public static string UnknownRoot => Get(nameof(UnknownRoot));

    /// <summary>Gets the string: Role of this host for an index.</summary>
    public static string RoleWriter => Get(nameof(RoleWriter));

    /// <summary>Gets the string: Role of this host for an index.</summary>
    public static string RoleReader => Get(nameof(RoleReader));

    /// <summary>Gets the string: Role of this host for an index that is not open.</summary>
    public static string RoleUnknown => Get(nameof(RoleUnknown));

    /// <summary>Gets the string: A count of a total, for example files done of files to do.</summary>
    public static string FilesOfFormat => Get(nameof(FilesOfFormat));

    /// <summary>Gets the string: A percentage.</summary>
    public static string PercentFormat => Get(nameof(PercentFormat));

    /// <summary>Gets the string: Versions an index was built with.</summary>
    public static string VersionsFormat => Get(nameof(VersionsFormat));

    /// <summary>Gets the string: Exclusion counts of a root.</summary>
    public static string ExcludedCountFormat => Get(nameof(ExcludedCountFormat));

    /// <summary>Gets the string: Screen-reader name of a root row.</summary>
    public static string RootRowNameFormat => Get(nameof(RootRowNameFormat));

    /// <summary>Gets the string: Label.</summary>
    public static string RootLabel => Get(nameof(RootLabel));

    /// <summary>Gets the string: Label of a root's role.</summary>
    public static string RoleLabel => Get(nameof(RoleLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string FilesLabel => Get(nameof(FilesLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string ChunksLabel => Get(nameof(ChunksLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string DiskLabel => Get(nameof(DiskLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string ModelLabel => Get(nameof(ModelLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string LastSweepLabel => Get(nameof(LastSweepLabel));

    /// <summary>Gets the string: Label.</summary>
    public static string ExcludedLabel => Get(nameof(ExcludedLabel));

    /// <summary>Gets the string: Button that rebuilds a root's index.</summary>
    public static string RebuildButton => Get(nameof(RebuildButton));

    /// <summary>Gets the string: Screen-reader name of a root's Rebuild button; {0} is the root.</summary>
    public static string RebuildRootNameFormat => Get(nameof(RebuildRootNameFormat));

    /// <summary>Gets the string: Tooltip of Rebuild.</summary>
    public static string RebuildToolTip => Get(nameof(RebuildToolTip));

    /// <summary>Gets the string: Button that deletes an index.</summary>
    public static string DeleteButton => Get(nameof(DeleteButton));

    /// <summary>Gets the string: Screen-reader name of a root's Delete button; {0} is the root.</summary>
    public static string DeleteRootNameFormat => Get(nameof(DeleteRootNameFormat));

    /// <summary>Gets the string: Tooltip of Delete.</summary>
    public static string DeleteToolTip => Get(nameof(DeleteToolTip));

    /// <summary>Gets the string: Shown after a rebuild started; {0} is the root.</summary>
    public static string RebuildStartedFormat => Get(nameof(RebuildStartedFormat));

    /// <summary>Gets the string: Shown when a rebuild could not start; {0} is the root, {1} the reason.</summary>
    public static string RebuildFailedFormat => Get(nameof(RebuildFailedFormat));

    /// <summary>Gets the string: Confirmation before deleting a root's index; {0} root, {1} disk use.</summary>
    public static string DeleteRootConfirmFormat => Get(nameof(DeleteRootConfirmFormat));

    /// <summary>Gets the string: Confirmation before deleting an index folder; {0} root, {1} disk use.</summary>
    public static string DeleteIndexConfirmFormat => Get(nameof(DeleteIndexConfirmFormat));

    /// <summary>Gets the string: Confirmation before deleting an index that is open here.</summary>
    public static string DeleteOpenIndexConfirmFormat => Get(nameof(DeleteOpenIndexConfirmFormat));

    /// <summary>Gets the string: Shown after a delete; {0} is the root.</summary>
    public static string DeletedFormat => Get(nameof(DeletedFormat));

    /// <summary>Gets the string: Shown when the host has no index with that id.</summary>
    public static string NothingToDeleteFormat => Get(nameof(NothingToDeleteFormat));

    /// <summary>Gets the string: Shown when a delete failed or was refused; {0} root, {1} reason.</summary>
    public static string DeleteFailedFormat => Get(nameof(DeleteFailedFormat));

    /// <summary>Gets the string: Label of the root picker on the Excluded files tab.</summary>
    public static string ExcludedRootLabel => Get(nameof(ExcludedRootLabel));

    /// <summary>Gets the string: Screen-reader name of the root picker.</summary>
    public static string ExcludedRootName => Get(nameof(ExcludedRootName));

    /// <summary>Gets the string: Check box that limits the list to secret-bearing files.</summary>
    public static string SecretOnlyCheck => Get(nameof(SecretOnlyCheck));

    /// <summary>Gets the string: Screen-reader name of the check box.</summary>
    public static string SecretOnlyName => Get(nameof(SecretOnlyName));

    /// <summary>Gets the string: Button that shows the previous page of excluded files.</summary>
    public static string PreviousPageButton => Get(nameof(PreviousPageButton));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string PreviousPageName => Get(nameof(PreviousPageName));

    /// <summary>Gets the string: Button that shows the next page of excluded files.</summary>
    public static string NextPageButton => Get(nameof(NextPageButton));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string NextPageName => Get(nameof(NextPageName));

    /// <summary>Gets the string: Button that copies the chosen file's path.</summary>
    public static string CopyPathButton => Get(nameof(CopyPathButton));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string CopyPathName => Get(nameof(CopyPathName));

    /// <summary>Gets the string: Button that opens the .pillionignore file of the chosen root.</summary>
    public static string OpenPillionIgnoreButton => Get(nameof(OpenPillionIgnoreButton));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OpenPillionIgnoreName => Get(nameof(OpenPillionIgnoreName));

    /// <summary>Gets the string: Tooltip.</summary>
    public static string OpenPillionIgnoreToolTip => Get(nameof(OpenPillionIgnoreToolTip));

    /// <summary>Gets the string: Screen-reader name of the list.</summary>
    public static string ExcludedListName => Get(nameof(ExcludedListName));

    /// <summary>Gets the string: Header of the column with the shield glyph.</summary>
    public static string ExcludedColumnShield => Get(nameof(ExcludedColumnShield));

    /// <summary>Gets the string: Column header.</summary>
    public static string ExcludedColumnPath => Get(nameof(ExcludedColumnPath));

    /// <summary>Gets the string: Column header.</summary>
    public static string ExcludedColumnKind => Get(nameof(ExcludedColumnKind));

    /// <summary>Gets the string: Column header.</summary>
    public static string ExcludedColumnReason => Get(nameof(ExcludedColumnReason));

    /// <summary>Gets the string: Column header.</summary>
    public static string ExcludedColumnDetails => Get(nameof(ExcludedColumnDetails));

    /// <summary>Gets the string: Shown on the Excluded files tab without a root.</summary>
    public static string ExcludedNoRoot => Get(nameof(ExcludedNoRoot));

    /// <summary>Gets the string: Shown when a root excludes nothing.</summary>
    public static string ExcludedNone => Get(nameof(ExcludedNone));

    /// <summary>Gets the string: Shown when the secret-only filter matches nothing.</summary>
    public static string ExcludedNoSecretFiles => Get(nameof(ExcludedNoSecretFiles));

    /// <summary>Gets the string: Page line; first and last entry shown, total.</summary>
    public static string ExcludedPageFormat => Get(nameof(ExcludedPageFormat));

    /// <summary>Gets the string: Shown when the list failed; {0} is the reason.</summary>
    public static string ExcludedFailedFormat => Get(nameof(ExcludedFailedFormat));

    /// <summary>Gets the string: Group header on the Excluded files list.</summary>
    public static string GroupSecretBearing => Get(nameof(GroupSecretBearing));

    /// <summary>Gets the string: Group header on the Excluded files list.</summary>
    public static string GroupOtherExclusions => Get(nameof(GroupOtherExclusions));

    /// <summary>Gets the string: Kind column.</summary>
    public static string KindSecretFile => Get(nameof(KindSecretFile));

    /// <summary>Gets the string: Kind column.</summary>
    public static string KindSecretFolder => Get(nameof(KindSecretFolder));

    /// <summary>Gets the string: Kind column.</summary>
    public static string KindFile => Get(nameof(KindFile));

    /// <summary>Gets the string: Kind column.</summary>
    public static string KindFolder => Get(nameof(KindFolder));

    /// <summary>Gets the string: Screen-reader name of the shield glyph.</summary>
    public static string ShieldName => Get(nameof(ShieldName));

    /// <summary>Gets the string: Screen-reader name of an excluded file row; path, kind, rule, details.</summary>
    public static string ExcludedRowNameFormat => Get(nameof(ExcludedRowNameFormat));

    /// <summary>Gets the string: Shown when the file could not be created or opened.</summary>
    public static string PillionIgnoreFailedFormat => Get(nameof(PillionIgnoreFailedFormat));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonSecretBearing => Get(nameof(ReasonSecretBearing));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonGitIgnore => Get(nameof(ReasonGitIgnore));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonPillionIgnore => Get(nameof(ReasonPillionIgnore));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonBuiltIn => Get(nameof(ReasonBuiltIn));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonUserExclude => Get(nameof(ReasonUserExclude));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonGeneratedFile => Get(nameof(ReasonGeneratedFile));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonMinifiedFile => Get(nameof(ReasonMinifiedFile));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonLockFile => Get(nameof(ReasonLockFile));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonGeneratedHeader => Get(nameof(ReasonGeneratedHeader));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonBinary => Get(nameof(ReasonBinary));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonTooLarge => Get(nameof(ReasonTooLarge));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonReparsePoint => Get(nameof(ReasonReparsePoint));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonNestedRepository => Get(nameof(ReasonNestedRepository));

    /// <summary>Gets the string: Rule that excluded a file.</summary>
    public static string ReasonUnreadable => Get(nameof(ReasonUnreadable));

    /// <summary>Gets the string: Rule that excluded a file when the host named none.</summary>
    public static string ReasonUnknown => Get(nameof(ReasonUnknown));

    /// <summary>Gets the string: Summary above the index list; count, disk use, stale count.</summary>
    public static string IndexesSummaryFormat => Get(nameof(IndexesSummaryFormat));

    /// <summary>Gets the string: Shown when there are none.</summary>
    public static string IndexesNone => Get(nameof(IndexesNone));

    /// <summary>Gets the string: Shown when the list failed; {0} is the reason.</summary>
    public static string IndexesFailedFormat => Get(nameof(IndexesFailedFormat));

    /// <summary>Gets the string: Screen-reader name of the list.</summary>
    public static string IndexesListName => Get(nameof(IndexesListName));

    /// <summary>Gets the string: Whether an index's repository root still exists.</summary>
    public static string RootExists => Get(nameof(RootExists));

    /// <summary>Gets the string: Whether an index's repository root still exists.</summary>
    public static string RootGone => Get(nameof(RootGone));

    /// <summary>Gets the string: Whether an index's repository root still exists.</summary>
    public static string RootUnknown => Get(nameof(RootUnknown));

    /// <summary>Gets the string: Whether an index is in use.</summary>
    public static string InUseOpen => Get(nameof(InUseOpen));

    /// <summary>Gets the string: Whether an index is in use.</summary>
    public static string InUseOtherHost => Get(nameof(InUseOtherHost));

    /// <summary>Gets the string: Whether an index is in use.</summary>
    public static string NotInUse => Get(nameof(NotInUse));

    /// <summary>Gets the string: Label.</summary>
    public static string LastUsedLabel => Get(nameof(LastUsedLabel));

    /// <summary>Gets the string: Label of whether the root exists.</summary>
    public static string RootExistsLabel => Get(nameof(RootExistsLabel));

    /// <summary>Gets the string: Label of whether the index is in use.</summary>
    public static string InUseLabel => Get(nameof(InUseLabel));

    /// <summary>Gets the string: Screen-reader name of an index folder row; root, disk use, root state, use.</summary>
    public static string IndexRowNameFormat => Get(nameof(IndexRowNameFormat));

    /// <summary>Gets the string: Screen-reader name of an index folder's Delete button.</summary>
    public static string DeleteIndexNameFormat => Get(nameof(DeleteIndexNameFormat));

    /// <summary>Gets the string: Tooltip.</summary>
    public static string DeleteIndexToolTip => Get(nameof(DeleteIndexToolTip));

    /// <summary>Gets the string: Status bar text while indexing; {0} is the percentage.</summary>
    public static string StatusBarIndexingFormat => Get(nameof(StatusBarIndexingFormat));

    /// <summary>Gets the string: Status bar text while indexing without a measure.</summary>
    public static string StatusBarIndexing => Get(nameof(StatusBarIndexing));

    /// <summary>Gets the string: Status bar text while paused; {0} is the short reason.</summary>
    public static string StatusBarPausedFormat => Get(nameof(StatusBarPausedFormat));

    /// <summary>Gets the string: Status bar text while paused.</summary>
    public static string StatusBarPaused => Get(nameof(StatusBarPaused));

    /// <summary>Gets the string: Status bar text after an indexing error.</summary>
    public static string StatusBarFailed => Get(nameof(StatusBarFailed));

    /// <summary>Gets the string: Screen-reader name and tooltip prefix of the status bar indicator.</summary>
    public static string StatusBarOpenName => Get(nameof(StatusBarOpenName));

    /// <summary>Gets the string: Summary sentence.</summary>
    public static string SummaryNoStatus => Get(nameof(SummaryNoStatus));

    /// <summary>Gets the string: Summary sentence.</summary>
    public static string SummaryFailed => Get(nameof(SummaryFailed));

    /// <summary>Gets the string: Summary sentence; {0} is the error.</summary>
    public static string SummaryFailedFormat => Get(nameof(SummaryFailedFormat));

    /// <summary>Gets the string: Summary sentence.</summary>
    public static string SummaryPaused => Get(nameof(SummaryPaused));

    /// <summary>Gets the string: Summary sentence; {0} is the reason.</summary>
    public static string SummaryPausedFormat => Get(nameof(SummaryPausedFormat));

    /// <summary>Gets the string: Summary sentence; {0} is the phase.</summary>
    public static string SummaryIndexingFormat => Get(nameof(SummaryIndexingFormat));

    /// <summary>Gets the string: Summary sentence.</summary>
    public static string SummaryIdle => Get(nameof(SummaryIdle));

    /// <summary>Gets the string: Summary sentence.</summary>
    public static string SummaryNoIndex => Get(nameof(SummaryNoIndex));

    /// <summary>Gets the string: Shown instead of the Indexing page while the indexWindow feature flag is off.</summary>
    public static string OptionsDisabledMessage => Get(nameof(OptionsDisabledMessage));

    /// <summary>Gets the string: Screen-reader name of the Indexing Options page.</summary>
    public static string OptionsPageName => Get(nameof(OptionsPageName));

    /// <summary>Gets the string: Label of the exclude box.</summary>
    public static string OptionsExcludeLabel => Get(nameof(OptionsExcludeLabel));

    /// <summary>Gets the string: Screen-reader name of the exclude box.</summary>
    public static string OptionsExcludeName => Get(nameof(OptionsExcludeName));

    /// <summary>Gets the string: Help under the exclude box.</summary>
    public static string OptionsExcludeHelp => Get(nameof(OptionsExcludeHelp));

    /// <summary>Gets the string: Label.</summary>
    public static string OptionsMaxMemoryLabel => Get(nameof(OptionsMaxMemoryLabel));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OptionsMaxMemoryName => Get(nameof(OptionsMaxMemoryName));

    /// <summary>Gets the string: Help.</summary>
    public static string OptionsMaxMemoryHelp => Get(nameof(OptionsMaxMemoryHelp));

    /// <summary>Gets the string: Check box.</summary>
    public static string OptionsPauseOnBattery => Get(nameof(OptionsPauseOnBattery));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OptionsPauseOnBatteryName => Get(nameof(OptionsPauseOnBatteryName));

    /// <summary>Gets the string: Label.</summary>
    public static string OptionsRetentionLabel => Get(nameof(OptionsRetentionLabel));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OptionsRetentionName => Get(nameof(OptionsRetentionName));

    /// <summary>Gets the string: Help.</summary>
    public static string OptionsRetentionHelp => Get(nameof(OptionsRetentionHelp));

    /// <summary>Gets the string: Read-only line; {0} is the model id.</summary>
    public static string OptionsModelFormat => Get(nameof(OptionsModelFormat));

    /// <summary>Gets the string: Read-only line when no index exists yet.</summary>
    public static string OptionsModelNone => Get(nameof(OptionsModelNone));

    /// <summary>Gets the string: Screen-reader name of the line.</summary>
    public static string OptionsModelName => Get(nameof(OptionsModelName));

    /// <summary>Gets the string: Validation message.</summary>
    public static string OptionsMaxMemoryInvalidFormat => Get(nameof(OptionsMaxMemoryInvalidFormat));

    /// <summary>Gets the string: Validation message.</summary>
    public static string OptionsRetentionInvalidFormat => Get(nameof(OptionsRetentionInvalidFormat));

    /// <summary>Gets the string: Button.</summary>
    public static string OptionsSave => Get(nameof(OptionsSave));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OptionsSaveName => Get(nameof(OptionsSaveName));

    /// <summary>Gets the string: Button.</summary>
    public static string OptionsReload => Get(nameof(OptionsReload));

    /// <summary>Gets the string: Screen-reader name.</summary>
    public static string OptionsReloadName => Get(nameof(OptionsReloadName));

    /// <summary>Formats a string from this class with the current culture.</summary>
    /// <param name="format">The format string, one of the <c>...Format</c> properties.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The text.</returns>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
