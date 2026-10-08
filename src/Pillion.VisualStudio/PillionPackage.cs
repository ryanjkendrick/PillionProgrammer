using System;
using System.ComponentModel.Design;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.Contracts;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio;

/// <summary>
/// The Pillion Programmer package. Loads in the background when a solution or folder is opened, writes a line to
/// the "Pillion Programmer" Output window pane, and registers the "Pillion: Show Status" command.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuids.PackageString)]
public sealed class PillionPackage : AsyncPackage
{
    private const string ProductName = "Pillion Programmer";

    /// <summary>Gets the extension's version (the assembly informational version).</summary>
    internal static string ExtensionVersion { get; } = ReadVersion();

    /// <inheritdoc/>
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(true);

        // Both services are free-threaded to request, but casting the COM output window must happen on the UI thread.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)).ConfigureAwait(true) as IVsOutputWindow;
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) as OleMenuCommandService;

        WriteLoadedMessage(outputWindow);

        if (commandService is not null)
        {
            var showStatusId = new CommandID(PackageGuids.CommandSet, PackageIds.ShowStatusCommand);
            commandService.AddCommand(new MenuCommand(OnShowStatus, showStatusId));
        }
    }

    private static string ReadVersion()
    {
        var assembly = typeof(PillionPackage).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static void WriteLoadedMessage(IVsOutputWindow? outputWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (outputWindow is null)
        {
            return;
        }

        var paneGuid = PackageGuids.OutputPane;
        if (ErrorHandler.Failed(outputWindow.CreatePane(ref paneGuid, ProductName, fInitVisible: 1, fClearWithSolution: 0))
            || ErrorHandler.Failed(outputWindow.GetPane(ref paneGuid, out var pane)))
        {
            return;
        }

        pane.OutputStringThreadSafe($"{ProductName} {ExtensionVersion} loaded (contract {ContractVersion.Text}).{Environment.NewLine}");
    }

    private void OnShowStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        VsShellUtilities.ShowMessageBox(
            this,
            $"{ProductName} {ExtensionVersion}{Environment.NewLine}Contract version {ContractVersion.Text}",
            ProductName,
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
