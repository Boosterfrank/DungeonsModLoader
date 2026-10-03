using System.Globalization;
using System.Windows.Data;

namespace DungeonsModLoader.App.Views.Shell;

/// <summary>
/// Multi-value converter returning true when the first two bound values are the same object instance.
/// The shell uses it to check the sidebar item whose page is the current page (one-way).
/// </summary>
public sealed class ReferenceEqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length >= 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException($"{nameof(ReferenceEqualityConverter)} is one-way.");
    }
}
