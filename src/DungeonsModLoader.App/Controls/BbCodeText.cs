using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Nexus.Text;
using Microsoft.Extensions.DependencyInjection;

namespace DungeonsModLoader.App.Controls;

/// <summary>
/// Attached property that renders Nexus BBCode into a <see cref="TextBlock"/>'s inlines:
/// <c>controls:BbCodeText.Source="{Binding Description}"</c>. Bold / italic / underline / headings / links /
/// list markers / image placeholders, all as text (no embedded browser).
/// </summary>
public static class BbCodeText
{
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
                    block.Inlines.Add(CreateLink("[image]", image.Url));
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
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += (_, args) =>
            {
                args.Handled = true;
                try
                {
                    App.Services.GetRequiredService<IWindowService>().OpenUrl(args.Uri.AbsoluteUri);
                }
                catch (InvalidOperationException)
                {
                    // Host not built (designer); nothing to open.
                }
            };
        }
        else
        {
            link.IsEnabled = false;
        }

        return link;
    }
}
