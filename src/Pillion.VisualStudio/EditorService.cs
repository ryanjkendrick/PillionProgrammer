using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio;

/// <summary>
/// The VSIX's <see cref="IEditorService"/> for phase 0: lists the open documents from the running document table and
/// refuses changes (<see cref="PillionErrorCode.PolicyDenied"/>, "Not implemented in phase 0").
/// </summary>
internal sealed class EditorService : NotImplementedEditorService
{
    private readonly AsyncPackage _package;

    /// <summary>Initializes a new instance of the <see cref="EditorService"/> class.</summary>
    /// <param name="package">The package (service provider and joinable task factory).</param>
    public EditorService(AsyncPackage package)
    {
        _package = package;
    }

    /// <inheritdoc/>
    public override async Task<OpenDocument[]> GetOpenDocumentsAsync(CancellationToken cancellationToken)
    {
        // The running document table and the selection service are main-thread objects.
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await _package.GetServiceAsync(typeof(SVsRunningDocumentTable)).ConfigureAwait(true) is not IVsRunningDocumentTable4 table4)
        {
            return [];
        }

        var table = new RunningDocumentTable(_package);
        var activePath = await GetActiveDocumentPathAsync().ConfigureAwait(true);

        var documents = new List<OpenDocument>();
        foreach (var info in table)
        {
            var path = info.Moniker;
            if (string.IsNullOrEmpty(path)
                || (info.Flags & (uint)_VSRDTFLAGS.RDT_ProjSlnDocument) != 0
                || !IsRootedFilePath(path))
            {
                continue;
            }

            documents.Add(new OpenDocument
            {
                Path = path,
                LanguageId = DocumentLanguage.FromPath(path),
                IsDirty = table4.IsDocumentDirty(info.DocCookie),
                IsActive = string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        return documents.ToArray();
    }

    private static bool IsRootedFilePath(string path)
    {
        try
        {
            return Path.IsPathRooted(path) && path.IndexOf("::", StringComparison.Ordinal) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<string?> GetActiveDocumentPathAsync()
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await _package.GetServiceAsync(typeof(SVsShellMonitorSelection)).ConfigureAwait(true) is not IVsMonitorSelection selection
            || ErrorHandler.Failed(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var value))
            || value is not IVsWindowFrame frame
            || ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var moniker)))
        {
            return null;
        }

        return moniker as string;
    }
}
