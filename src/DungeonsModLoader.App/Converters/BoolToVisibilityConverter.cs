using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DungeonsModLoader.App.Converters;

/// <summary>
/// bool -> Visibility. <see cref="Invert"/> flips the mapping; <see cref="FalseVisibility"/> picks
/// Collapsed (default) or Hidden for the "false" case.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public Visibility FalseVisibility { get; set; } = Visibility.Collapsed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : FalseVisibility;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return Invert ? !visible : visible;
    }
}
