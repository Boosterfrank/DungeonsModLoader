using System.Windows;
using System.Windows.Media;

namespace DungeonsModLoader.App.Views.Shell;

/// <summary>
/// Attached properties consumed by the sidebar <c>NavItem</c> style (ShellStyles.xaml):
/// <c>shell:NavItem.Icon</c> supplies the 16px glyph geometry drawn left of the label.
/// </summary>
public static class NavItem
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(NavItem), new FrameworkPropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);
}
