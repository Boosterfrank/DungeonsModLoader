using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DungeonsModLoader.App.Controls;

/// <summary>
/// Attached properties that let the button variants (Primary / Secondary / Danger / Play) share one
/// "extruded" control template. The template reads them with TemplateBinding; the styles set them.
/// </summary>
public static class ButtonHelper
{
    /// <summary>Brush of the darker 3D edge drawn under the button face (the logo-style extrusion).</summary>
    public static readonly DependencyProperty ExtrudeBrushProperty = DependencyProperty.RegisterAttached(
        "ExtrudeBrush",
        typeof(Brush),
        typeof(ButtonHelper),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Effect faded in behind the button while the mouse is over it (one of the Effect.Glow.* tokens).</summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached(
        "Glow",
        typeof(Effect),
        typeof(ButtonHelper),
        new FrameworkPropertyMetadata(null));

    [AttachedPropertyBrowsableForType(typeof(ButtonBase))]
    public static Brush? GetExtrudeBrush(DependencyObject element) => (Brush?)element.GetValue(ExtrudeBrushProperty);

    public static void SetExtrudeBrush(DependencyObject element, Brush? value) => element.SetValue(ExtrudeBrushProperty, value);

    [AttachedPropertyBrowsableForType(typeof(ButtonBase))]
    public static Effect? GetGlow(DependencyObject element) => (Effect?)element.GetValue(GlowProperty);

    public static void SetGlow(DependencyObject element, Effect? value) => element.SetValue(GlowProperty, value);
}
