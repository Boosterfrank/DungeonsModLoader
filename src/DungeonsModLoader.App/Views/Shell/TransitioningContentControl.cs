using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DungeonsModLoader.App.Views.Shell;

/// <summary>
/// A <see cref="ContentControl"/> that plays a short fade + slide-up (Opacity 0 → 1, Y 12 → 0 over
/// <c>Duration.Normal</c>, decelerating) whenever its <see cref="ContentControl.Content"/> changes.
/// Used by the shell for page transitions.
/// </summary>
public class TransitioningContentControl : ContentControl
{
    private const double SlideDistance = 12;
    private static readonly Duration FallbackDuration = new(TimeSpan.FromMilliseconds(200));

    private readonly TranslateTransform _slide = new();

    public TransitioningContentControl()
    {
        RenderTransform = _slide;
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (newContent is null)
        {
            return;
        }

        var duration = TryFindResource("Duration.Normal") is Duration d && d.HasTimeSpan ? d : FallbackDuration;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        var slide = new DoubleAnimation(SlideDistance, 0, duration) { EasingFunction = easing };

        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        _slide.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }
}
