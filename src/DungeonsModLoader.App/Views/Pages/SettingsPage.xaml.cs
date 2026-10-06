using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DungeonsModLoader.App.ViewModels.Pages;

namespace DungeonsModLoader.App.Views.Pages;

/// <summary>
/// Settings page. DataContext is the <c>SettingsViewModel</c> (set by the shell's page DataTemplate).
/// The code-behind only bridges the <see cref="PasswordBox"/> of the API key editor (whose Password is not
/// bindable) to the view model, in both directions.
/// </summary>
public partial class SettingsPage : UserControl
{
    private PasswordBox? _keyBox;
    private SettingsViewModel? _viewModel;
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>The template's PasswordBox announces itself when it is created (it lives inside a DataTemplate).</summary>
    private void OnKeyBoxLoaded(object sender, RoutedEventArgs e)
    {
        _keyBox = (PasswordBox)sender;
        SyncBoxFromViewModel();
    }

    private void OnKeyBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _viewModel is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            _viewModel.ApiKeyInput = ((PasswordBox)sender).Password;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.ApiKeyInput) or nameof(SettingsViewModel.ShowApiKey))
        {
            SyncBoxFromViewModel();
        }
    }

    private void SyncBoxFromViewModel()
    {
        if (_syncing || _keyBox is null || _viewModel is null || _keyBox.Password == _viewModel.ApiKeyInput)
        {
            return;
        }

        _syncing = true;
        try
        {
            _keyBox.Password = _viewModel.ApiKeyInput;
        }
        finally
        {
            _syncing = false;
        }
    }
}
