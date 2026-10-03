using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DungeonsModLoader.App.Converters;

/// <summary>
/// null / empty string -> Collapsed, anything else -> Visible. <see cref="Invert"/> flips the mapping
/// (useful for empty states: "show this when there is nothing to list").
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && value is not string { Length: 0 };
        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
