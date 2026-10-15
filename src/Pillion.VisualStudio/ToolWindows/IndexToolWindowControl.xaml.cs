using System.Windows.Controls;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>The Pillion Index tool window's content. All behaviour is in <see cref="IndexWindowViewModel"/>.</summary>
internal sealed partial class IndexToolWindowControl : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="IndexToolWindowControl"/> class.</summary>
    /// <param name="viewModel">The Index window's view model.</param>
    public IndexToolWindowControl(IndexWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus into the window (its first button), when it is shown.</summary>
    public void FocusFirst() => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
}
