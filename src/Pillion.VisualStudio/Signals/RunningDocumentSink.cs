using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Receives Running Document Table events and reports document opened, saved, closed and the active document to the
/// hub. Called on the UI thread; only the moniker lookup (a dictionary read in the table) happens here.
/// </summary>
internal sealed class RunningDocumentSink(IdeSignalsHub hub, IVsRunningDocumentTable4 table) : IVsRunningDocTableEvents3
{
    /// <inheritdoc/>
    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (PathOf(docCookie) is { } path)
        {
            hub.NotifyDocumentOpened(path);
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dwReadLocksRemaining == 0 && dwEditLocksRemaining == 0 && PathOf(docCookie) is { } path)
        {
            hub.NotifyDocumentClosed(path);
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (PathOf(docCookie) is { } path)
        {
            hub.NotifyDocumentSaved(path);
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // A document tab becoming visible is the editor's "active document changed".
        if (PathOf(docCookie) is { } path)
        {
            hub.NotifyActiveDocumentChanged(path);
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc/>
#pragma warning disable CA1822 // Interface implementation.
    public int OnAfterSaveAll() => VSConstants.S_OK;
#pragma warning restore CA1822

    /// <inheritdoc/>
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;

    /// <inheritdoc/>
    public int OnBeforeSave(uint docCookie) => VSConstants.S_OK;

    private string? PathOf(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var moniker = table.GetDocumentMoniker(docCookie);

            // Monikers of documents that are not files (a Miscellaneous Files placeholder, a URL) have no usable path.
            return !string.IsNullOrEmpty(moniker) && Path.IsPathRooted(moniker) ? moniker : null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
