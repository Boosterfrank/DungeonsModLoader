using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DungeonsModLoader.App.ViewModels.Dialogs;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>
/// "Connect your Nexus Mods account" dialog. Shown modally by <see cref="Services.NexusConnectPrompt"/>; the
/// code-behind bridges the PasswordBox (whose Password is not bindable) and closes the window when the view model
/// asks. <see cref="NexusConnectViewModel.Connected"/> says whether a key was stored.
/// </summary>
public partial class NexusConnectDialog : Window
{
    private readonly NexusConnectViewModel _viewModel;
    private bool _syncing;

    public NexusConnectDialog(NexusConnectViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        if (_viewModel.ShowApiKey)
        {
            KeyText.Focus();
        }
        else
        {
            KeyBox.Focus();
        }
    }

    private void OnCloseRequested(object? sender, bool connected)
    {
        try
        {
            DialogResult = connected;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnKeyBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
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
        if (e.PropertyName is not (nameof(NexusConnectViewModel.ApiKeyInput) or nameof(NexusConnectViewModel.ShowApiKey)) || _syncing)
        {
            return;
        }

        // Paste and the post-connect clear change the view model first; mirror them into the box.
        if (KeyBox.Password != _viewModel.ApiKeyInput)
        {
            _syncing = true;
            try
            {
                KeyBox.Password = _viewModel.ApiKeyInput;
            }
            finally
            {
                _syncing = false;
            }
        }

        if (e.PropertyName == nameof(NexusConnectViewModel.ShowApiKey))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_viewModel.ShowApiKey)
                {
                    KeyText.Focus();
                    KeyText.CaretIndex = KeyText.Text.Length;
                }
                else
                {
                    KeyBox.Focus();
                }
            });
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Released before the drag started.
        }
    }
}
