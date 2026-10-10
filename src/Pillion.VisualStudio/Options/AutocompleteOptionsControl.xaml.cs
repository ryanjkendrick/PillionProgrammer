using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>The Autocomplete page's content. All behaviour is in <see cref="AutocompleteOptionsViewModel"/>.</summary>
internal sealed partial class AutocompleteOptionsControl : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="AutocompleteOptionsControl"/> class.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public AutocompleteOptionsControl(AutocompleteOptionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

/// <summary>Tools &gt; Options &gt; Pillion Programmer &gt; Autocomplete.</summary>
[Guid(PackageGuids.AutocompleteOptionPageString)]
internal sealed class AutocompleteOptionPage : HostSettingsDialogPage
{
    /// <inheritdoc/>
    protected override OptionsPageViewModel CreateViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log) =>
        new AutocompleteOptionsViewModel(connection, joinableTaskFactory, log);

    /// <inheritdoc/>
    protected override UIElement CreateView(OptionsPageViewModel viewModel) => new AutocompleteOptionsControl((AutocompleteOptionsViewModel)viewModel);
}
