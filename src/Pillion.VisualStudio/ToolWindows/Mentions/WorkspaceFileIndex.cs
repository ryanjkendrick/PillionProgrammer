using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The solution or folder file list for <c>@file</c> and <c>@folder</c> (work item 1.9; the index replaces it in phase 2).
/// For a solution it walks every loaded project's hierarchy through <see cref="IVsSolution"/> (on the UI thread, yielding
/// every few hundred items); for an open folder it enumerates the folder on a background thread. Files under <c>bin</c>,
/// <c>obj</c>, <c>.git</c>, <c>node_modules</c> and <c>.vs</c> are left out, and at most <see cref="MaxFiles"/> are kept.
/// The list is cached, collected again after solution, project and folder events (debounced) and when it is older than
/// <see cref="StaleAfter"/> on the next request.
/// </summary>
internal sealed class WorkspaceFileIndex : IWorkspaceFiles, IVsSolutionEvents, IVsSolutionEvents7, IDisposable
{
    /// <summary>The most files kept.</summary>
    public const int MaxFiles = 20000;

    /// <summary>How old a list may be before the next request collects it again (files added in a project raise no event here).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", "node_modules", ".vs" };

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly PillionLog? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposal = new();
    private WorkspaceFileList _current = WorkspaceFileList.Empty;
    private DateTime _collectedAt = DateTime.MinValue;
    private bool _dirty = true;
    private JoinableTask<WorkspaceFileList>? _collecting;
    private IVsSolution? _solution;
    private uint _cookie;
    private int _advised;
    private int _changeVersion;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="WorkspaceFileIndex"/> class.</summary>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The extension log; file names are never logged.</param>
    public WorkspaceFileIndex(JoinableTaskFactory joinableTaskFactory, PillionLog? log)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _log = log;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public WorkspaceFileList Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    public async Task<WorkspaceFileList> GetAsync(CancellationToken cancellationToken)
    {
        await EnsureAdvisedAsync(cancellationToken).ConfigureAwait(false);
        JoinableTask<WorkspaceFileList> collecting;
        lock (_gate)
        {
            if (!_dirty && DateTime.UtcNow - _collectedAt < StaleAfter)
            {
                return _current;
            }

            _collecting ??= _joinableTaskFactory.RunAsync(CollectAndPublishAsync);
            collecting = _collecting;
        }

        return await collecting.JoinAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposal.Cancel();
        if (Interlocked.Exchange(ref _advised, 0) == 1 && _solution is { } solution && _cookie != 0)
        {
            try
            {
                _joinableTaskFactory.Run(async () =>
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync();
                    _ = solution.UnadviseSolutionEvents(_cookie);
                });
            }
#pragma warning disable CA1031 // Visual Studio may be shutting down; the events die with it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Debug("chat.files.unadvise_failed", "Could not stop following solution events.", ("error", ex.GetType().Name));
            }
        }
    }

    /// <inheritdoc/>
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => MarkDirty();

    /// <inheritdoc/>
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => MarkDirty();

    /// <inheritdoc/>
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => MarkDirty();

    /// <inheritdoc/>
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => MarkDirty();

    /// <inheritdoc/>
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => MarkDirty();

    /// <inheritdoc/>
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnAfterCloseSolution(object pUnkReserved) => MarkDirty();

    /// <inheritdoc/>
    public void OnAfterOpenFolder(string folderPath) => MarkDirty();

    /// <inheritdoc/>
    public void OnBeforeCloseFolder(string folderPath)
    {
    }

    /// <inheritdoc/>
    public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
    {
    }

    /// <inheritdoc/>
    public void OnAfterCloseFolder(string folderPath) => MarkDirty();

    /// <inheritdoc/>
    public void OnAfterLoadAllDeferredProjects() => MarkDirty();

    private static bool IsSkipped(string path) =>
        path.Split('\\', '/').Any(SkippedFolders.Contains);

    private static uint ToItemId(object? value) => value switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        short s => unchecked((uint)s),
        long l => unchecked((uint)l),
        _ => VSConstants.VSITEMID_NIL,
    };

    private static List<string> EnumerateFolder(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    files.Add(file);
                    if (files.Count >= MaxFiles)
                    {
                        break;
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An unreadable folder is left out.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private int MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
        }

        // Collect again after a quiet moment, so opening a solution with many projects collects once.
        var version = Interlocked.Increment(ref _changeVersion);
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(Debounce, _disposal.Token).ConfigureAwait(false);
                if (version == Volatile.Read(ref _changeVersion) && !_disposed)
                {
                    await GetAsync(_disposal.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
#pragma warning disable CA1031 // A failed collection keeps the last list; the next request tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Warning("chat.files.failed", "Collecting the solution's file list failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        });
        return VSConstants.S_OK;
    }

    private async Task EnsureAdvisedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _advised) == 1 || _disposed)
        {
            return;
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (Interlocked.CompareExchange(ref _advised, 1, 0) != 0)
        {
            return;
        }

        _solution = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(true) as IVsSolution;
        if (_solution is not null && ErrorHandler.Succeeded(_solution.AdviseSolutionEvents(this, out var cookie)))
        {
            _cookie = cookie;
        }
    }

    private async Task<WorkspaceFileList> CollectAndPublishAsync()
    {
        try
        {
            var list = await CollectAsync(_disposal.Token).ConfigureAwait(false);
            bool changed;
            lock (_gate)
            {
                changed = !list.Roots.SequenceEqual(_current.Roots, StringComparer.OrdinalIgnoreCase)
                    || !list.Files.SequenceEqual(_current.Files, StringComparer.OrdinalIgnoreCase);
                Volatile.Write(ref _current, list);
                _collectedAt = DateTime.UtcNow;
                _dirty = false;
            }

            _log?.Debug("chat.files.collected", "Collected the solution's file list.", ("roots", list.Roots.Count), ("files", list.Files.Count));
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return list;
        }
        finally
        {
            lock (_gate)
            {
                _collecting = null;
            }
        }
    }

    private async Task<WorkspaceFileList> CollectAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = _solution ?? await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(true) as IVsSolution;
        if (solution is null || ErrorHandler.Failed(solution.GetSolutionInfo(out var directory, out _, out _)) || string.IsNullOrEmpty(directory))
        {
            return WorkspaceFileList.Empty;
        }

        var root = directory.TrimEnd('\\', '/');
        var folderMode = ErrorHandler.Succeeded(solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var mode)) && mode is true;
        if (folderMode)
        {
            await TaskScheduler.Default;
            return new WorkspaceFileList([root], EnumerateFolder(root, cancellationToken));
        }

        var roots = new List<string> { root };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hierarchies = new List<IVsHierarchy>();
        var flags = (uint)(__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION | __VSENUMPROJFLAGS.EPF_ALLINSOLUTION);
        var type = Guid.Empty;
        if (ErrorHandler.Succeeded(solution.GetProjectEnum(flags, ref type, out var projects)) && projects is not null)
        {
            var buffer = new IVsHierarchy[1];
            while (projects.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                hierarchies.Add(buffer[0]);
            }
        }

        var visited = 0;
        foreach (var hierarchy in hierarchies)
        {
            if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out var projectFile))
                && Path.IsPathRooted(projectFile)
                && Path.GetDirectoryName(projectFile) is { Length: > 0 } projectDirectory
                && !roots.Contains(projectDirectory, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(projectDirectory);
            }

            var pending = new Stack<uint>();
            pending.Push(VSConstants.VSITEMID_ROOT);
            while (pending.Count > 0 && files.Count < MaxFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = pending.Pop();
                if (++visited % 300 == 0)
                {
                    // Give the UI thread back now and then: a large solution has tens of thousands of items.
                    await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);
                }

                if (item != VSConstants.VSITEMID_ROOT)
                {
                    if (ErrorHandler.Failed(hierarchy.GetCanonicalName(item, out var name)) || string.IsNullOrEmpty(name) || !Path.IsPathRooted(name))
                    {
                        continue; // References, dependencies and other virtual nodes: not files, and not walked into.
                    }

                    if (IsSkipped(name))
                    {
                        continue;
                    }

                    if (!name.EndsWith("\\", StringComparison.Ordinal) && !name.EndsWith("/", StringComparison.Ordinal))
                    {
                        files.Add(name);
                    }
                }

                if (ErrorHandler.Succeeded(hierarchy.GetProperty(item, (int)__VSHPROPID.VSHPROPID_FirstChild, out var child)))
                {
                    for (var next = ToItemId(child); next != VSConstants.VSITEMID_NIL;)
                    {
                        pending.Push(next);
                        next = ErrorHandler.Succeeded(hierarchy.GetProperty(next, (int)__VSHPROPID.VSHPROPID_NextSibling, out var sibling))
                            ? ToItemId(sibling)
                            : VSConstants.VSITEMID_NIL;
                    }
                }
            }
        }

        // Linked files from outside every root (NuGet content, shared SDK files) are left out: they are not the solution's.
        var kept = files.Where(f => roots.Any(r => f.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        kept.Sort(StringComparer.OrdinalIgnoreCase);
        return new WorkspaceFileList(roots, kept);
    }
}
