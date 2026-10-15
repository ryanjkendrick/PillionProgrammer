using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>The state <see cref="PillionPackage"/> hands to <see cref="IndexToolWindow"/> when it creates the window.</summary>
internal sealed class IndexToolWindowState
{
    /// <summary>Initializes a new instance of the <see cref="IndexToolWindowState"/> class.</summary>
    /// <param name="viewModel">The Index window's view model (not yet started).</param>
    public IndexToolWindowState(IndexWindowViewModel viewModel)
    {
        ViewModel = viewModel;
    }

    /// <summary>Gets the view model.</summary>
    public IndexWindowViewModel ViewModel { get; }
}

/// <summary>
/// The Pillion Index tool window (View &gt; Other Windows &gt; Pillion Index, Tools &gt; Pillion: Open Index, or a click on the
/// status bar's indexing indicator; hidden while the <c>indexWindow</c> flag is off). Created asynchronously by
/// <see cref="PillionPackage"/>, which supplies an <see cref="IndexToolWindowState"/>.
/// </summary>
[Guid(PackageGuids.IndexToolWindowString)]
internal sealed class IndexToolWindow : ToolWindowPane
{
    /// <summary>The window caption.</summary>
    public const string Title = "Pillion Index";

    private readonly IndexWindowViewModel _viewModel;
    private readonly IndexToolWindowControl _control;

    /// <summary>Initializes a new instance of the <see cref="IndexToolWindow"/> class. Runs on the UI thread.</summary>
    /// <param name="state">The state from <see cref="PillionPackage"/>.</param>
    public IndexToolWindow(IndexToolWindowState state)
        : base(null)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = Title;
        _viewModel = state.ViewModel;
        _control = new IndexToolWindowControl(_viewModel);
        Content = _control;
        _viewModel.Start();
    }

    /// <inheritdoc/>
    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        _control.FocusFirst();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewModel.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Asks a yes or no question in a message box (default No) and returns whether the developer chose Yes. Call on the UI thread.</summary>
    /// <param name="question">The question.</param>
    /// <returns><see langword="true"/> for Yes.</returns>
    public static bool Confirm(string question)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            question,
            IndexStrings.WindowTitle,
            OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
        return result == (int)VSConstants.MessageBoxResult.IDYES;
    }

    /// <summary>Opens a root's <c>.pillionignore</c> in the editor, creating it (with a one-line comment) when it does not exist. Call on the UI thread.</summary>
    /// <param name="rootPath">The repository root.</param>
    public static void OpenPillionIgnore(string rootPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = PillionIgnoreFile.EnsureExists(rootPath);
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
    }
}
