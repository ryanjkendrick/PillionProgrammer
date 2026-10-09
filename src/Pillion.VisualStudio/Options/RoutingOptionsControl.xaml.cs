using System.Windows.Controls;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>The Models and routing page's content. All behaviour is in <see cref="RoutingOptionsViewModel"/>.</summary>
internal sealed partial class RoutingOptionsControl : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="RoutingOptionsControl"/> class.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public RoutingOptionsControl(RoutingOptionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
