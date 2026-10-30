using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ocean.App.Controls;

/// <summary>
/// Attached "spinner" for icons:  &lt;TextBlock c:Spin.IsSpinning="{Binding IsRefreshing}" .../&gt;
/// While true the element rotates continuously. When it turns false the current turn is completed and the icon
/// eases to rest upright, so even an instant refresh shows one full, smooth rotation.
/// </summary>
public static class Spin
{
    private static readonly Duration Turn = new(TimeSpan.FromMilliseconds(700));

    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.RegisterAttached(
        "IsSpinning", typeof(bool), typeof(Spin), new PropertyMetadata(false, OnChanged));

    public static bool GetIsSpinning(DependencyObject d) => (bool)d.GetValue(IsSpinningProperty);
    public static void SetIsSpinning(DependencyObject d, bool value) => d.SetValue(IsSpinningProperty, value);

    // Per element: true while a stop was requested before the current turn finished.
    private static readonly DependencyProperty StopRequestedProperty = DependencyProperty.RegisterAttached(
        "StopRequested", typeof(bool), typeof(Spin), new PropertyMetadata(false));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var rotate = Ensure(el);
        if ((bool)e.NewValue)
        {
            d.SetValue(StopRequestedProperty, false);
            if (rotate.HasAnimatedProperties) return; // already turning: just keep going
            StartTurn(el, rotate);
        }
        else
        {
            d.SetValue(StopRequestedProperty, true); // finish this turn, then rest
        }
    }

    private static RotateTransform Ensure(UIElement el)
    {
        if (el.RenderTransform is RotateTransform r) return r;
        r = new RotateTransform(0);
        el.RenderTransform = r;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return r;
    }

    /// <summary>One full turn; at its end either another turn (still spinning) or a soft stop.</summary>
    private static void StartTurn(UIElement el, RotateTransform rotate)
    {
        var anim = new DoubleAnimation(0, 360, Turn) { FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) =>
        {
            var keepGoing = GetIsSpinning(el) && !(bool)el.GetValue(StopRequestedProperty);
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
            if (keepGoing) StartTurn(el, rotate);
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }
}
