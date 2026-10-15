using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// What is open in Visual Studio (the solution file and its loaded projects, or an opened folder), for the indexing relay,
/// which hands it to the host with <see cref="IContextService.SetWorkspaceAsync"/>. The Visual Studio implementation reads
/// <c>IVsSolution</c>; tests supply a fixed description.
/// </summary>
public interface IWorkspaceDescriptionSource
{
    /// <summary>
    /// Raised, on any thread, when the solution, its projects or the opened folder may have changed. It may come in bursts
    /// (opening a solution reports every project); the relay debounces it and reads <see cref="GetAsync"/> afterwards.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Reads what is open now. May visit the UI thread in short batches; never blocks it.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The description; an empty one (no solution, no projects, no folder) when nothing is open.</returns>
    Task<WorkspaceDescription> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One source of IDE activity flags beyond builds (which come from <see cref="IIdeSignals.BuildStateChanged"/>): the debugger,
/// and later test runs. The indexing relay ORs <see cref="Current"/> into the set it reports to the host.
/// </summary>
public interface IIdeActivitySource
{
    /// <summary>Gets the activities this source sees in progress now (for example <see cref="IdeActivity.Debug"/>). Safe on any thread.</summary>
    IdeActivity Current { get; }

    /// <summary>Raised, on any thread, when <see cref="Current"/> may have changed.</summary>
    event EventHandler? Changed;
}
