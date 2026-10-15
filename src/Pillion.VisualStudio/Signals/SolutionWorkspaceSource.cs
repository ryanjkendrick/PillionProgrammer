using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// What is open in Visual Studio, for the indexing relay (work item 2.9): the solution file and its loaded projects' files,
/// or the folder opened with Open Folder, read through <see cref="IVsSolution"/>. Only the project roots are read (one
/// document moniker per project, never the items), on the UI thread, yielding every few projects. <see cref="Changed"/>
/// follows the solution, project and folder events (<see cref="IVsSolutionEvents"/>, <see cref="IVsSolutionEvents7"/>);
/// the relay debounces it.
/// </summary>
internal sealed class SolutionWorkspaceSource : IWorkspaceDescriptionSource, IVsSolutionEvents, IVsSolutionEvents7
{
    /// <summary>How many projects are read before the UI thread is given back.</summary>
    private const int ProjectsPerBatch = 50;

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IVsSolution _solution;
    private readonly PillionLog? _log;

    /// <summary>Initializes a new instance of the <see cref="SolutionWorkspaceSource"/> class.</summary>
    /// <param name="joinableTaskFactory">Switches to the UI thread.</param>
    /// <param name="solution">The solution service; advise this object for its events.</param>
    /// <param name="log">The extension log; file names are never logged.</param>
    public SolutionWorkspaceSource(JoinableTaskFactory joinableTaskFactory, IVsSolution solution, PillionLog? log)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _solution = solution;
        _log = log;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public async Task<WorkspaceDescription> GetAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        List<IVsHierarchy> hierarchies;
        WorkspaceDescription description;
        using (UiThreadTimings.Measure("indexing.workspace.read"))
        {
            if (ErrorHandler.Failed(_solution.GetSolutionInfo(out var directory, out var file, out _)))
            {
                return new WorkspaceDescription();
            }

            var folderMode = ErrorHandler.Succeeded(_solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var mode)) && mode is true;
            if (folderMode)
            {
                return new WorkspaceDescription { FolderPath = Rooted(directory) };
            }

            description = new WorkspaceDescription { SolutionPath = Rooted(file) };
            if (description.SolutionPath is null)
            {
                return new WorkspaceDescription(); // No solution is open (or it was never saved).
            }

            hierarchies = [];
            var type = Guid.Empty;
            if (ErrorHandler.Succeeded(_solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref type, out var projects)) && projects is not null)
            {
                var buffer = new IVsHierarchy[1];
                while (projects.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
                {
                    hierarchies.Add(buffer[0]);
                }
            }
        }

        var paths = new List<string>(hierarchies.Count);
        for (var start = 0; start < hierarchies.Count; start += ProjectsPerBatch)
        {
            if (start > 0)
            {
                // A solution with hundreds of projects: give the UI thread back between batches.
                await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);
            }

            using var timing = UiThreadTimings.Measure("indexing.workspace.projects");
            for (var i = start; i < hierarchies.Count && i < start + ProjectsPerBatch; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Rooted(RootDocument(hierarchies[i])) is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        description.ProjectPaths = [.. paths];
        _log?.Debug("indexing.workspace.read", "Read the open solution.", ("projects", paths.Count));
        return description;
    }

    /// <inheritdoc/>
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => RaiseChanged();

    /// <inheritdoc/>
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => RaiseChanged();

    /// <inheritdoc/>
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => RaiseChanged();

    /// <inheritdoc/>
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => RaiseChanged();

    /// <inheritdoc/>
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => RaiseChanged();

    /// <inheritdoc/>
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnAfterCloseSolution(object pUnkReserved) => RaiseChanged();

    /// <inheritdoc/>
    public void OnAfterOpenFolder(string folderPath) => RaiseChanged();

    /// <inheritdoc/>
    public void OnBeforeCloseFolder(string folderPath)
    {
    }

    /// <inheritdoc/>
    public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
    {
    }

    /// <inheritdoc/>
    public void OnAfterCloseFolder(string folderPath) => RaiseChanged();

    /// <inheritdoc/>
    public void OnAfterLoadAllDeferredProjects() => RaiseChanged();

    /// <summary>The path when it is absolute, without a trailing separator; otherwise <see langword="null"/>.</summary>
    private static string? Rooted(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathRooted(path))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null; // Characters that are not valid in a path: a virtual node, not a file.
        }

        var trimmed = path!.TrimEnd('\\', '/');
        return trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal) ? path : trimmed;
    }

    /// <summary>
    /// The project's root document moniker: its project file (as the user wrote it; classic projects report their canonical
    /// names in lower case), or its folder for a project without a file. Solution folders and other virtual nodes give no
    /// absolute path and are left out by the caller.
    /// </summary>
    private static string? RootDocument(IVsHierarchy hierarchy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (hierarchy is IVsProject project && ErrorHandler.Succeeded(project.GetMkDocument(VSConstants.VSITEMID_ROOT, out var document)) && !string.IsNullOrEmpty(document))
        {
            return document;
        }

        return ErrorHandler.Succeeded(hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out var name)) ? name : null;
    }

    /// <summary>Called on the UI thread by the solution events: the relay's handler only notes it (the read comes later, debounced).</summary>
    private int RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return VSConstants.S_OK;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // Never throw back into Visual Studio's event source.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Warning("indexing.workspace.handler_failed", "A workspace change listener threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }

        return VSConstants.S_OK;
    }
}
