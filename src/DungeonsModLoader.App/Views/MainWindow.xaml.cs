using System.Windows;
using DungeonsModLoader.App.ViewModels;

namespace DungeonsModLoader.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
