using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Text;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Controls;

/// <summary>
/// Attached property that renders Nexus BBCode into a <see cref="TextBlock"/>'s inlines:
/// <c>controls:BbCodeText.Source="{Binding Description}"</c>. Bold / italic / underline / headings / links / list
/// markers as text, <c>[img]</c> tags as inline pictures (downloaded through the thumbnail cache, scaled to the
/// text width; a link when the picture cannot be shown). No embedded browser.
/// </summary>
public static class BbCodeText
{
    /// <summary>Pictures in a description never grow taller than this (they keep their aspect ratio).</summary>
    private const double MaxImageHeight = 520;

    /// <summary>Wider pictures are decoded down to this many pixels (screenshots are often 1920 wide).</summary>
    private const int MaxDecodeWidth = 1400;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(BbCodeText),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    [AttachedPropertyBrowsableForType(typeof(TextBlock))]
    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        var nodes = BbCode.Parse(e.NewValue as string);
        var baseSize = block.FontSize;
        var muted = block.TryFindResource("Brush.Text.Muted") as Brush;

        foreach (var node in nodes)
        {
            switch (node)
            {
                case BbText text:
                    block.Inlines.Add(CreateText(text, baseSize));
                    break;
                case BbLineBreak:
                    block.Inlines.Add(new LineBreak());
                    break;
                case BbListItem item:
                    block.Inlines.Add(new Run(item.Ordered ? $"{item.Index}. " : "•  "));
                    break;
                case BbImage image:
                    AddImage(block, image.Url);
                    break;
                case BbRule:
                    block.Inlines.Add(new Run("────────") { Foreground = muted ?? Brushes.Gray });
                    break;
            }
        }
    }

    private static Inline CreateText(BbText text, double baseSize)
    {
        var run = new Run(text.Text);
        if (text.Bold)
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (text.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        var decorations = new TextDecorationCollection();
        if (text.Underline)
        {
            decorations.Add(TextDecorations.Underline);
        }

        if (text.Strikethrough)
        {
            decorations.Add(TextDecorations.Strikethrough);
        }

        if (decorations.Count > 0)
        {
            run.TextDecorations = decorations;
        }

        if (text.HeadingLevel > 0)
        {
            run.FontSize = text.HeadingLevel switch
            {
                1 => baseSize * 1.45,
                2 => baseSize * 1.25,
                _ => baseSize * 1.1,
            };
            run.FontWeight = FontWeights.SemiBold;
        }

        if (!string.IsNullOrWhiteSpace(text.Url))
        {
            var link = CreateLink(null, text.Url);
            link.Inlines.Add(run);
            return link;
        }

        return run;
    }

    private static Hyperlink CreateLink(string? caption, string url)
    {
        var link = new Hyperlink();
        if (caption is not null)
        {
            link.Inlines.Add(new Run(caption));
        }

        link.ToolTip = url;
        if (IsWebUrl(url, out var uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += (_, args) =>
            {
                args.Handled = true;
                OpenUrl(args.Uri.AbsoluteUri);
            };
        }
        else
        {
            link.IsEnabled = false;
        }

        return link;
    }

    // ------------------------------------------------------------------------------------------------------
    // Inline pictures
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds an <c>[img]</c> as a picture on its own line: a placeholder-free <see cref="Image"/> that fills in once
    /// the thumbnail cache has the file. Falls back to an "[image]" link when the URL is not http(s), the host is
    /// not built (designer) or the file cannot be decoded.
    /// </summary>
    private static void AddImage(TextBlock block, string url)
    {
        IThumbnailCache? cache = null;
        if (IsWebUrl(url, out var uri))
        {
            try
            {
                cache = App.Services.GetService<IThumbnailCache>();
            }
            catch (InvalidOperationException)
            {
                // Host not built (designer).
            }
        }

        if (cache is null || uri is null)
        {
            block.Inlines.Add(CreateLink("[image]", url));
            return;
        }

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxHeight = MaxImageHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = "Open the picture in your browser",
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(FrameworkElement.MaxWidthProperty, new Binding(nameof(FrameworkElement.ActualWidth)) { Source = block });
        image.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl(uri.AbsoluteUri);
        };

        // Block-level: the picture sits on its own line.
        if (block.Inlines.LastInline is not null and not LineBreak)
        {
            block.Inlines.Add(new LineBreak());
        }

        var container = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
        block.Inlines.Add(container);
        block.Inlines.Add(new LineBreak());

        _ = LoadImageAsync(cache, uri, image, container, block, url);
    }

    private static async Task LoadImageAsync(IThumbnailCache cache, Uri uri, Image image, InlineUIContainer container, TextBlock block, string url)
    {
        BitmapSource? bitmap = null;
        try
        {
            var path = await cache.GetFileAsync(uri.AbsoluteUri);
            if (path is not null)
            {
                // Decoded off the UI thread and frozen so it can be handed over.
                bitmap = await Task.Run(() => Decode(path));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"BbCodeText: picture {uri} could not be loaded: {ex.Message}");
        }

        if (bitmap is not null)
        {
            image.Source = bitmap;
            return;
        }

        // Could not be shown: a link in its place (if the text block still has it).
        try
        {
            if (ReferenceEquals(container.Parent, block))
            {
                block.Inlines.InsertAfter(container, CreateLink("[image]", url));
                block.Inlines.Remove(container);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The description was replaced meanwhile; nothing to swap.
        }
    }

    /// <summary>Decodes a cached picture file, scaling very wide ones down; null when the file is not a picture.</summary>
    private static BitmapSource? Decode(string path)
    {
        try
        {
            int width;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                width = decoder.Frames.Count > 0 ? decoder.Frames[0].PixelWidth : 0;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            if (width > MaxDecodeWidth)
            {
                bitmap.DecodePixelWidth = MaxDecodeWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsWebUrl(string url, out Uri? uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            App.Services.GetRequiredService<IWindowService>().OpenUrl(url);
        }
        catch (InvalidOperationException)
        {
            // Host not built (designer); nothing to open.
        }
    }
}
