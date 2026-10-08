using System.Windows.Controls;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>The Privacy page's content. All behaviour is in <see cref="PrivacyOptionsViewModel"/>.</summary>
internal sealed partial class PrivacyOptionsControl : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="PrivacyOptionsControl"/> class.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public PrivacyOptionsControl(PrivacyOptionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
