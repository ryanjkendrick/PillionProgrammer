using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>
/// The package-owned part of inline chat (work items 1.6 and 1.7): the <c>inlineChat</c> feature flag, what is
/// remembered between prompts, and the "Pillion: Inline Chat" command (Tools menu, editor context menu, and the chord
/// Ctrl+Alt+Shift+P, I). The command opens an <see cref="InlineChatController"/> on the active document view.
/// </summary>
internal sealed class InlineChatFeature : IDisposable
{
    private readonly AsyncPackage _package;

    /// <summary>Initializes a new instance of the <see cref="InlineChatFeature"/> class.</summary>
    /// <param name="package">The package (services and the joinable task factory).</param>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="log">The extension log.</param>
    /// <param name="output">Writes a line to the Pillion Output pane, or <see langword="null"/>.</param>
    public InlineChatFeature(AsyncPackage package, IHostConnection connection, PillionLog log, Action<string>? output)
    {
        _package = package;
        Connection = connection;
        Log = log;
        Output = output;
        Availability = new InlineChatAvailability(connection, log);
    }

    /// <summary>Gets the connection to Pillion Host.</summary>
    public IHostConnection Connection { get; }

    /// <summary>Gets the extension log.</summary>
    public PillionLog Log { get; }

    /// <summary>Gets the writer for the Pillion Output pane, or <see langword="null"/>.</summary>
    public Action<string>? Output { get; set; }

    /// <summary>Gets the <c>inlineChat</c> feature flag.</summary>
    public InlineChatAvailability Availability { get; }

    /// <summary>Gets what inline chat remembers for as long as Visual Studio runs (recent prompts, model, model list).</summary>
    public InlineChatHistory History { get; } = new();

    /// <summary>Gets the package's joinable task factory.</summary>
    public Microsoft.VisualStudio.Threading.JoinableTaskFactory JoinableTaskFactory => _package.JoinableTaskFactory;

    /// <summary>Starts following the host's feature flags.</summary>
    public void Start() => Availability.Start();

    /// <summary>Creates the "Pillion: Inline Chat" command; disabled while the <c>inlineChat</c> flag is off.</summary>
    /// <returns>The command, for the package's command service.</returns>
    public OleMenuCommand CreateCommand()
    {
        var command = new OleMenuCommand(OnExecute, new CommandID(PackageGuids.CommandSet, PackageIds.InlineChatCommand));
        command.BeforeQueryStatus += (sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand menuCommand)
            {
                menuCommand.Visible = true;
                menuCommand.Enabled = Availability.IsEnabled;
            }

            // Menus are where users look after changing settings: ask the host again.
            _ = _package.JoinableTaskFactory.RunAsync(() => Availability.RefreshAsync(_package.DisposalToken));
        };
        return command;
    }

    /// <inheritdoc/>
    public void Dispose() => Availability.Dispose();

    private void OnExecute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Availability.IsEnabled)
        {
            Output?.Invoke(InlineChatAvailability.DisabledMessage + ".");
            return;
        }

        if (Package.GetGlobalService(typeof(SComponentModel)) is not IComponentModel componentModel
            || Package.GetGlobalService(typeof(SVsTextManager)) is not IVsTextManager textManager)
        {
            Log.Warning("inlinechat.services.missing", "The editor services are not available.");
            return;
        }

        var services = componentModel.GetService<InlineChatEditorServices>();
        if (ErrorHandler.Failed(textManager.GetActiveView(fMustHaveFocus: 0, pBuffer: null, out var activeView))
            || activeView is null
            || services.Adapters.GetWpfTextView(activeView) is not { } view
            || !InlineChatController.IsDocumentView(view))
        {
            Output?.Invoke("Pillion: Inline Chat works in a code editor. Open a document and try again.");
            return;
        }

        InlineChatController.GetOrCreate(view, services, this).Open();
    }
}
