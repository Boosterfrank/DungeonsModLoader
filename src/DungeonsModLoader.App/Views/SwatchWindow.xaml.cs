using System.Windows;
using DungeonsModLoader.App.ViewModels;

namespace DungeonsModLoader.App.Views;

/// <summary>
/// Developer tool (opened with <c>--swatch</c> or from Settings > "Theme swatches"): shows the palette,
/// typography and every styled control so the theme can be reviewed in one place.
/// </summary>
public partial class SwatchWindow : Window
{
    public SwatchWindow()
    {
        InitializeComponent();
        DataContext = new SwatchViewModel();
    }
}
