using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DungeonsModLoader.App.Converters;

/// <summary>
/// Local image file path -> frozen <see cref="BitmapImage"/> (or an <see cref="ImageBrush"/> when <see cref="AsBrush"/>
/// is set, for rounded thumbnails). The converter parameter is the decode width in pixels; the file is read fully
/// so the cache can replace it later. Unreadable files yield null (the placeholder stays visible).
/// </summary>
public sealed class FilePathToImageConverter : IValueConverter
{
    public bool AsBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            if (parameter is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0)
            {
                image.DecodePixelWidth = width;
            }

            image.EndInit();
            image.Freeze();

            if (!AsBrush)
            {
                return image;
            }

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
