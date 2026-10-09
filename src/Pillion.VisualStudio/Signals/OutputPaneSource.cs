using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Reads Output window panes. The Build and Debug panes are found through <see cref="IVsOutputWindow"/> by their well-known
/// GUIDs (which gives their localized names); the text itself is read through the pane's text document in the DTE object
/// model, from the end, so a large build log costs only the tail. Other panes (Tests) are found by name. Runs on the UI
/// thread for the length of one read.
/// </summary>
internal sealed class OutputPaneSource(IVsOutputWindow outputWindow, DTE dte, JoinableTaskFactory joinableTaskFactory) : IOutputPaneSource
{
    /// <inheritdoc/>
    public async Task<string?> ReadTailAsync(string paneName, int maxChars, CancellationToken cancellationToken)
    {
        await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var name = ResolveName(paneName);
        var pane = FindPane(name);
        if (pane is null)
        {
            return null;
        }

        var document = pane.TextDocument;
        if (document is null)
        {
            return null;
        }

        var end = document.EndPoint;
        var start = end.CreateEditPoint();

        // One character more than asked for, so text that is longer than the request is recognised as longer.
        start.MoveToAbsoluteOffset(Math.Max(1, end.AbsoluteCharOffset - maxChars - 1));
        return start.GetText(end).Replace("\r\n", "\n");
    }

    /// <summary>Maps Build and Debug to the names Visual Studio gives those panes (they are localized).</summary>
    private string ResolveName(string paneName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Guid? guid =
            string.Equals(paneName, "Build", StringComparison.OrdinalIgnoreCase) ? VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid
            : string.Equals(paneName, "Debug", StringComparison.OrdinalIgnoreCase) ? VSConstants.OutputWindowPaneGuid.DebugPane_guid
            : null;
        if (guid is { } paneGuid
            && ErrorHandler.Succeeded(outputWindow.GetPane(ref paneGuid, out var pane))
            && pane is not null
            && ErrorHandler.Succeeded(pane.GetName(ref paneName)))
        {
            return paneName;
        }

        return paneName;
    }

    private OutputWindowPane? FindPane(string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var panes = ((EnvDTE80.DTE2)dte).ToolWindows.OutputWindow.OutputWindowPanes;
        foreach (OutputWindowPane pane in panes)
        {
            if (string.Equals(pane.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return pane;
            }
        }

        return null;
    }
}
