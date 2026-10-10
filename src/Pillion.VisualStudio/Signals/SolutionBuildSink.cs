using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Receives the solution build manager's update events (build, rebuild and clean all go through them) and reports them
/// to the hub. Called on the UI thread; each method only records a time stamp and queues work, so a build is never slowed.
/// </summary>
internal sealed class SolutionBuildSink(IdeSignalsHub hub) : IVsUpdateSolutionEvents2
{
    /// <inheritdoc/>
    public int UpdateSolution_Begin(ref int pfCancelUpdate)
    {
        hub.NotifyBuildStarted();
        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
    {
        hub.NotifyBuildFinished(fSucceeded != 0 && fCancelCommand == 0);
        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int UpdateSolution_Cancel() => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel)
    {
        hub.NotifyProjectBuildStarted();
        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => VSConstants.S_OK;
}
