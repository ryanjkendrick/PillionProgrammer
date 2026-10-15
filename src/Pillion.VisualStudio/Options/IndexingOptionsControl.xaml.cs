using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>The Indexing page's content. All behaviour is in <see cref="IndexingOptionsViewModel"/>.</summary>
internal sealed partial class IndexingOptionsControl : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="IndexingOptionsControl"/> class.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public IndexingOptionsControl(IndexingOptionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

/// <summary>Tools &gt; Options &gt; Pillion Programmer &gt; Indexing.</summary>
[Guid(PackageGuids.IndexingOptionPageString)]
internal sealed class IndexingOptionPage : HostSettingsDialogPage
{
    /// <inheritdoc/>
    protected override OptionsPageViewModel CreateViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log) =>
        new IndexingOptionsViewModel(connection, joinableTaskFactory, log, PillionPackage.Instance?.Availability);

    /// <inheritdoc/>
    protected override UIElement CreateView(OptionsPageViewModel viewModel) => new IndexingOptionsControl((IndexingOptionsViewModel)viewModel);
}
