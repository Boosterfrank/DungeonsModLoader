using System.Globalization;
using System.Windows.Data;

namespace DungeonsModLoader.App.Converters;

/// <summary>Negates a boolean in both directions (e.g. IsEnabled="{Binding IsBusy, Converter=...}").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
