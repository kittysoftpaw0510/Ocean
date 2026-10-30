using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ocean.App.Controls;

/// <summary>
/// Uniform-tile wrap panel with UI virtualization and pixel scrolling: only the visible rows (plus one
/// row of buffer) have containers, so a share with thousands of videos stays light and smooth.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;
    private double _leftPad;

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren; // touching it initializes the generator
        var count = ItemCount;
        var width = double.IsInfinity(availableSize.Width) ? (_viewport.Width > 0 ? _viewport.Width : ItemWidth) : availableSize.Width;
        _columns = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        _leftPad = Math.Max(0, (width - _columns * ItemWidth) / 2);
        var rows = (int)Math.Ceiling(count / (double)_columns);
        var extent = new Size(width, rows * ItemHeight);
        var viewportHeight = double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height;
        UpdateScroll(extent, new Size(width, viewportHeight));

        if (count == 0)
        {
            CleanUp(-1, -1);
            return new Size(width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        }

        var firstRow = (int)Math.Floor(_offset.Y / ItemHeight);
        var lastRow = (int)Math.Ceiling((_offset.Y + viewportHeight) / ItemHeight);
        var first = Math.Max(0, (firstRow - 1) * _columns);
        var last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(first);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child) break;
                if (newlyRealized)
                {
                    if (childIndex >= children.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        CleanUp(first, last);
        return new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var row = index / _columns;
            var col = index % _columns;
            InternalChildren[i].Arrange(new Rect(_leftPad + col * ItemWidth - _offset.X, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(pos);
            if (index < first || index > last)
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.Position.Index >= 0 && args.ItemUICount > 0 && args.Position.Index + args.ItemUICount <= InternalChildren.Count)
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                SetVerticalOffset(0);
                break;
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        var row = index / Math.Max(1, _columns);
        var top = row * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    // ------------------------------------------------------------------ IScrollInfo

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private const double LineStep = 56;

    public void LineUp() => SetVerticalOffset(_offset.Y - LineStep);
    public void LineDown() => SetVerticalOffset(_offset.Y + LineStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - LineStep * SystemParameters.WheelScrollLines);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + LineStep * SystemParameters.WheelScrollLines);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Clamp(double.IsNaN(offset) ? 0 : offset, 0, max);
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child == visual || (visual is DependencyObject d && child.IsAncestorOf(d)))
            {
                var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                BringIndexIntoView(index);
                break;
            }
        }
        return rectangle;
    }

    private void UpdateScroll(Size extent, Size viewport)
    {
        var changed = false;
        if (extent != _extent) { _extent = extent; changed = true; }
        if (viewport != _viewport) { _viewport = viewport; changed = true; }
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > max) { _offset.Y = max; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }
}
