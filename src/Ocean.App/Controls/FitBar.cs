using System.Windows;
using System.Windows.Controls;

namespace Ocean.App.Controls;

/// <summary>
/// A bar with a left group (first child) and a right group (second child), e.g. a breadcrumb and its buttons.
/// When both fit side by side they share one line, the right group at the right edge. When they don't (a narrow
/// window, a long path, a button that appeared) the right group moves to a second line, still right-aligned,
/// instead of being drawn over the left group. A left group wider than the whole bar is cut at the bar's edge.
/// </summary>
public sealed class FitBar : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(FitBar),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(nameof(RowGap), typeof(double), typeof(FitBar),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Least space between the groups on one line.</summary>
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    /// <summary>Space between the lines when the right group moved down.</summary>
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    /// <summary>True while the right group is on its own line.</summary>
    public bool IsWrapped { get; private set; }

    private UIElement? Left => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Right => InternalChildren.Count > 1 ? InternalChildren[1] : null;

    protected override Size MeasureOverride(Size available)
    {
        // natural widths: how wide each group wants to be
        var unbounded = new Size(double.PositiveInfinity, available.Height);
        Left?.Measure(unbounded);
        Right?.Measure(unbounded);
        var l = Left?.DesiredSize ?? default;
        var r = Right?.DesiredSize ?? default;
        var gap = l.Width > 0 && r.Width > 0 ? Gap : 0;
        var oneLine = l.Width + gap + r.Width;
        IsWrapped = !double.IsInfinity(available.Width) && oneLine > available.Width && l.Width > 0 && r.Width > 0;
        var width = double.IsInfinity(available.Width) ? oneLine : available.Width;
        return IsWrapped ? new Size(width, l.Height + RowGap + r.Height) : new Size(width, Math.Max(l.Height, r.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var l = Left?.DesiredSize ?? default;
        var r = Right?.DesiredSize ?? default;
        var w = final.Width;
        if (IsWrapped)
        {
            Left?.Arrange(new Rect(0, 0, Math.Min(l.Width, w), l.Height));
            var rw = Math.Min(r.Width, w);
            Right?.Arrange(new Rect(w - rw, l.Height + RowGap, rw, r.Height));
        }
        else
        {
            var h = Math.Max(l.Height, r.Height);
            Right?.Arrange(new Rect(Math.Max(0, w - r.Width), (h - r.Height) / 2, Math.Min(r.Width, w), r.Height));
            var room = Math.Max(0, w - r.Width - (r.Width > 0 ? Gap : 0)); // never under the right group
            Left?.Arrange(new Rect(0, (h - l.Height) / 2, Math.Min(l.Width, room), l.Height));
        }
        return final;
    }
}
