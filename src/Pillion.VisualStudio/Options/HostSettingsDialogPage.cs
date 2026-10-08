using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>
/// Base class of the Tools &gt; Options &gt; Pillion Programmer pages. The pages are a view over the host's settings file,
/// not Visual Studio settings: nothing is stored in the registry, the page loads from Pillion Host each time it is shown
/// (unless it has unsaved edits), and OK saves any edits in the background. The UI thread never waits on the host.
/// </summary>
[DesignerCategory("Code")]
internal abstract class HostSettingsDialogPage : UIElementDialogPage
{
    private UIElement? _child;
    private OptionsPageViewModel? _viewModel;
    private PillionPackage? _package;

    /// <inheritdoc/>
    protected override UIElement Child => _child ??= CreateChild();

    /// <inheritdoc/>
    public override void LoadSettingsFromStorage()
    {
        // The settings live in Pillion Host's settings.json, not in the Visual Studio settings store.
    }

    /// <inheritdoc/>
    public override void SaveSettingsToStorage()
    {
        // Saved through ISettingsService; see OnApply.
    }

    /// <summary>Creates the page's view model.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The extension log.</param>
    /// <returns>The view model.</returns>
    protected abstract OptionsPageViewModel CreateViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log);

    /// <summary>Creates the page's view.</summary>
    /// <param name="viewModel">The view model from <see cref="CreateViewModel"/>.</param>
    /// <returns>The view.</returns>
    protected abstract UIElement CreateView(OptionsPageViewModel viewModel);

    /// <inheritdoc/>
    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);
        _ = Child;
        if (_viewModel is { IsDirty: false } viewModel)
        {
            Run(viewModel.LoadAsync);
        }
    }

    /// <inheritdoc/>
    protected override void OnApply(PageApplyEventArgs e)
    {
        if (e is not null && e.ApplyBehavior == ApplyKind.Apply && _viewModel is { IsLoaded: true, IsDirty: true } viewModel)
        {
            Run(viewModel.SaveAsync);
        }

        base.OnApply(e);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewModel?.Dispose();
        }

        base.Dispose(disposing);
    }

    private UIElement CreateChild()
    {
        _package = PillionPackage.Instance;
        if (_package?.HostConnection is not { } connection)
        {
            return new TextBlock
            {
                Text = "Pillion Programmer is still loading. Close Options and open this page again.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            };
        }

        _viewModel = CreateViewModel(connection, _package.JoinableTaskFactory, _package.Log);
        return CreateView(_viewModel);
    }

    private void Run(Func<Task> work)
    {
        if (_package is not { } package)
        {
            return;
        }

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The page or Visual Studio is closing.
            }
#pragma warning disable CA1031 // A failure in a settings page must not take Visual Studio down; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                package.Log?.Error("options.page.failed", "An Options page operation failed.", ("page", GetType().Name), ("error", ex.GetType().Name));
            }
        });
    }
}

/// <summary>Tools &gt; Options &gt; Pillion Programmer &gt; Providers and keys.</summary>
[Guid(PackageGuids.ProvidersOptionPageString)]
internal sealed class ProvidersOptionPage : HostSettingsDialogPage
{
    /// <inheritdoc/>
    protected override OptionsPageViewModel CreateViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log) =>
        new ProvidersOptionsViewModel(connection, joinableTaskFactory, new CredentialManagerStore(), log);

    /// <inheritdoc/>
    protected override UIElement CreateView(OptionsPageViewModel viewModel) => new ProvidersOptionsControl((ProvidersOptionsViewModel)viewModel);
}

/// <summary>Tools &gt; Options &gt; Pillion Programmer &gt; Privacy.</summary>
[Guid(PackageGuids.PrivacyOptionPageString)]
internal sealed class PrivacyOptionPage : HostSettingsDialogPage
{
    /// <inheritdoc/>
    protected override OptionsPageViewModel CreateViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log) =>
        new PrivacyOptionsViewModel(connection, joinableTaskFactory, log);

    /// <inheritdoc/>
    protected override UIElement CreateView(OptionsPageViewModel viewModel) => new PrivacyOptionsControl((PrivacyOptionsViewModel)viewModel);
}
