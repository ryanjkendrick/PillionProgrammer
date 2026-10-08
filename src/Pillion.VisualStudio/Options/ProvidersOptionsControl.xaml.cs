using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Options;

/// <summary>
/// The Providers and keys page's content. <see cref="PasswordBox.Password"/> cannot be bound, so the key is copied to
/// <see cref="ProviderItemViewModel.ApiKey"/> here, and the box is cleared when the view model clears the key.
/// </summary>
internal sealed partial class ProvidersOptionsControl : UserControl
{
    private readonly ProvidersOptionsViewModel _viewModel;
    private ProviderItemViewModel? _watched;
    private bool _syncing;

    /// <summary>Initializes a new instance of the <see cref="ProvidersOptionsControl"/> class.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public ProvidersOptionsControl(ProvidersOptionsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Watch(_viewModel.SelectedProvider);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProvidersOptionsViewModel.SelectedProvider))
        {
            Watch(_viewModel.SelectedProvider);
        }
    }

    private void Watch(ProviderItemViewModel? item)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnItemPropertyChanged;
        }

        _watched = item;
        if (item is not null)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        SyncPasswordBox();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderItemViewModel.ApiKey))
        {
            SyncPasswordBox();
        }
    }

    private void SyncPasswordBox()
    {
        var key = _watched?.ApiKey ?? string.Empty;
        if (KeyBox.Password == key)
        {
            return;
        }

        _syncing = true;
        try
        {
            KeyBox.Password = key;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _watched is not null)
        {
            _watched.ApiKey = KeyBox.Password;
        }
    }
}
