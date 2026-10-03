using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.ComponentModel;
using DungeonsModLoader.Core;

namespace DungeonsModLoader.App.ViewModels;

/// <summary>
/// Backing data for <see cref="Views.SwatchWindow"/>, the design-review tool that shows the palette,
/// typography and control styles before the whole app is styled.
/// Everything is read from the live application resources (Themes/Colors.xaml, Themes/Typography.xaml),
/// so the window always reflects the current tokens; only group membership and filler text live here.
/// No DI dependencies: the window constructs this itself.
/// </summary>
public sealed partial class SwatchViewModel : ObservableObject
{
    private const string ColorPrefix = "Color.";

    /// <summary>Palette groups in display order. A token joins the first group whose prefix matches.</summary>
    private static readonly (string Name, string[] Prefixes)[] ColorGroupDefinitions =
    {
        ("Backgrounds", new[] { "Color.Bg." }),
        ("Borders & bevels", new[] { "Color.Border", "Color.Bevel." }),
        ("Accent", new[] { "Color.Accent" }),
        ("Cyan", new[] { "Color.Cyan" }),
        ("Semantic", new[] { "Color.Success", "Color.Warning", "Color.Danger", "Color.Purple" }),
        ("Text", new[] { "Color.Text." }),
    };

    private static readonly string[] GradientKeys =
    {
        "Brush.Window.Background",
        "Brush.Sidebar.Background",
        "Brush.Panel.Gradient",
        "Brush.Accent.Gradient",
        "Brush.Accent.GradientHover",
        "Brush.Accent.GradientPressed",
        "Brush.Danger.Gradient",
        "Brush.Success.Gradient",
    };

    private static readonly string[] EffectKeys = { "Effect.Glow.Accent", "Effect.Glow.Cyan", "Effect.Shadow.Panel" };

    private static readonly string[] TypographyKeys =
    {
        "Text.Display", "Text.H1", "Text.H2", "Text.H3", "Text.Body",
        "Text.Secondary", "Text.Small", "Text.Caption", "Text.Label", "Text.Mono",
    };

    private static readonly (string Label, FontWeight Weight)[] WeightSamples =
    {
        ("Regular", FontWeights.Regular),
        ("Medium", FontWeights.Medium),
        ("SemiBold", FontWeights.SemiBold),
        ("Bold", FontWeights.Bold),
    };

    private static readonly string[] ShapeKeys = { "Radius.Small", "Radius.Medium", "Radius.Large" };

    public SwatchViewModel()
    {
        ColorGroups = BuildColorGroups();
        Gradients = GradientKeys.Select(key => new BrushSample(key, FindResource<Brush>(key) ?? Brushes.Transparent)).ToList();
        Effects = EffectKeys.Select(key => new EffectSample(key, FindResource<Effect>(key))).ToList();
        TypographySamples = TypographyKeys.Select(key => BuildTypographySample(key, SampleText)).ToList();
        DisplayFontWeights = BuildFontSamples("Font.Display", "Pixelify Sans");
        BodyFontWeights = BuildFontSamples("Font.Body", "Inter");
        Shapes = BuildShapeSamples();
        ScrollSampleText = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 24).Select(i => $"Line {i:00}  -  long content so the themed scrollbar is visible"));
    }

    public string WindowTitle { get; } = $"{AppInfo.DisplayName} - Theme swatches";

    public string Subtitle { get; } =
        $"Palette, typography and control styles for the {AppInfo.GameDisplayName} look  ·  v{AppInfo.Version}";

    /// <summary>Pangram used for every typography row.</summary>
    public string SampleText { get; } =
        $"{AppInfo.GameDisplayName} mod manager - The quick brown fox jumps over the lazy dog 0123456789";

    public IReadOnlyList<ColorGroup> ColorGroups { get; }

    public IReadOnlyList<BrushSample> Gradients { get; }

    public IReadOnlyList<EffectSample> Effects { get; }

    public IReadOnlyList<TypographySample> TypographySamples { get; }

    public IReadOnlyList<FontSample> DisplayFontWeights { get; }

    public IReadOnlyList<FontSample> BodyFontWeights { get; }

    public IReadOnlyList<ShapeSample> Shapes { get; }

    /// <summary>Filler for the ComboBox sample (profile names).</summary>
    public IReadOnlyList<string> ComboItems { get; } = new[] { "Default", "Hardcore", "Skins only" };

    /// <summary>Filler for the ListBox sample (mod folder names from the ~mods example).</summary>
    public IReadOnlyList<string> ListItems { get; } = new[] { "BlueprintLoader", "CustomSkinLoader", "FreeCam", "SecondSkinLayer" };

    /// <summary>Filler for the ScrollViewer sample; long enough to force a vertical scrollbar.</summary>
    public string ScrollSampleText { get; }

    // ----------------------------------------------------------------------------------------------
    // Builders
    // ----------------------------------------------------------------------------------------------

    private static IReadOnlyList<ColorGroup> BuildColorGroups()
    {
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        if (Application.Current?.Resources is { } root)
        {
            CollectColors(root, colors);
        }

        var buckets = ColorGroupDefinitions
            .Select(definition => (definition.Name, Swatches: new List<ColorSwatch>()))
            .ToList();
        var other = new List<ColorSwatch>();

        foreach (var (key, color) in colors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var swatch = new ColorSwatch(key, FormatHex(color), CreateBrush(color));
            var index = Array.FindIndex(
                ColorGroupDefinitions,
                definition => definition.Prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)));

            if (index >= 0)
            {
                buckets[index].Swatches.Add(swatch);
            }
            else
            {
                other.Add(swatch);
            }
        }

        var groups = buckets
            .Where(bucket => bucket.Swatches.Count > 0)
            .Select(bucket => new ColorGroup(bucket.Name, bucket.Swatches))
            .ToList();

        if (other.Count > 0)
        {
            groups.Add(new ColorGroup("Other", other));
        }

        return groups;
    }

    /// <summary>
    /// Walks a dictionary and its merged dictionaries. Later merged dictionaries override earlier ones and
    /// a dictionary's own entries override everything it merges, which mirrors WPF's lookup order.
    /// </summary>
    private static void CollectColors(ResourceDictionary dictionary, Dictionary<string, Color> into)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            CollectColors(merged, into);
        }

        foreach (var key in dictionary.Keys)
        {
            if (key is string name
                && name.StartsWith(ColorPrefix, StringComparison.Ordinal)
                && dictionary[key] is Color color)
            {
                into[name] = color;
            }
        }
    }

    private static TypographySample BuildTypographySample(string key, string sampleText)
    {
        var style = FindResource<Style>(key);

        FontFamily? family = null;
        double? size = null;
        FontWeight? weight = null;

        // Derived style setters win over BasedOn setters, so keep the first value found.
        for (var current = style; current is not null; current = current.BasedOn)
        {
            foreach (var setter in current.Setters.OfType<Setter>())
            {
                if (setter.Property == TextBlock.FontFamilyProperty)
                {
                    family ??= setter.Value as FontFamily;
                }
                else if (setter.Property == TextBlock.FontSizeProperty)
                {
                    size ??= setter.Value as double?;
                }
                else if (setter.Property == TextBlock.FontWeightProperty)
                {
                    weight ??= setter.Value as FontWeight?;
                }
            }
        }

        var details = style is null
            ? $"{key}  ·  (style not found)"
            : $"{key}  ·  {FamilyName(family)}  ·  {size?.ToString("0") ?? "?"}px"
              + (weight is { } w && w != FontWeights.Normal ? $"  ·  {w}" : string.Empty);

        return new TypographySample(key, sampleText, style, details);
    }

    private static IReadOnlyList<FontSample> BuildFontSamples(string familyKey, string fallbackFamily)
    {
        var family = FindResource<FontFamily>(familyKey) ?? new FontFamily(fallbackFamily);
        return WeightSamples.Select(sample => new FontSample(sample.Label, family, sample.Weight)).ToList();
    }

    private static IReadOnlyList<ShapeSample> BuildShapeSamples()
    {
        var extrude = FindValue<double>("Size.Extrude") ?? 3;
        var bevel = new Thickness(1, 1, 1, extrude);

        return ShapeKeys
            .Select(key =>
            {
                var radius = FindValue<CornerRadius>(key) ?? new CornerRadius(0);
                return new ShapeSample(key, radius, bevel, $"radius {radius.TopLeft:0} · border 1 · extrude {extrude:0}");
            })
            .ToList();
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------

    private static T? FindResource<T>(string key) where T : class
        => Application.Current?.TryFindResource(key) as T;

    private static T? FindValue<T>(string key) where T : struct
        => Application.Current?.TryFindResource(key) is T value ? value : null;

    private static string FormatHex(Color color)
        => color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>"pack://application:,,,/Assets/Fonts/#Pixelify Sans" -> "Pixelify Sans".</summary>
    private static string FamilyName(FontFamily? family)
    {
        var source = family?.Source ?? "?";
        var hash = source.LastIndexOf('#');
        return hash >= 0 ? source[(hash + 1)..] : source;
    }
}

/// <summary>One Color.* token: key, "#RRGGBB" text and a frozen brush for the tile.</summary>
public sealed record ColorSwatch(string Key, string Hex, Brush Brush);

/// <summary>A named palette group (Backgrounds, Accent, ...) with its tokens sorted by key.</summary>
public sealed record ColorGroup(string Name, IReadOnlyList<ColorSwatch> Colors);

/// <summary>A gradient (or any brush) token shown as a wide bar.</summary>
public sealed record BrushSample(string Key, Brush Brush);

/// <summary>An Effect.* token applied to a small panel.</summary>
public sealed record EffectSample(string Key, Effect? Effect);

/// <summary>One Text.* TextBlock style with the pangram and a "key · family · size" description.</summary>
public sealed record TypographySample(string Key, string Sample, Style? Style, string Details);

/// <summary>A raw font family at one weight.</summary>
public sealed record FontSample(string Label, FontFamily Family, FontWeight Weight);

/// <summary>A Radius.* token rendered as a beveled, extruded tile.</summary>
public sealed record ShapeSample(string Key, CornerRadius Radius, Thickness BorderThickness, string Details);
