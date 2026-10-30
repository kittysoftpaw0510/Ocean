using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ocean.Core;

namespace Ocean.App.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool Collapse { get; set; } = true;
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : (Collapse ? Visibility.Collapsed : Visibility.Hidden);
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var has = value is not null && !(value is string s && s.Length == 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var n = value switch { int i => i, long l => l, double d => d, _ => 0 };
        var show = n > 0;
        if (Invert) show = !show;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is long l ? Format.Bytes(l) : value is int i ? Format.Bytes(i) : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not null && p is not null && string.Equals(value.ToString(), p.ToString(), StringComparison.OrdinalIgnoreCase);
    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is true ? Enum.Parse(t, p!.ToString()!, true) : Binding.DoNothing;
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    // Invariant formatting so numbers compare the same on every locale ("1.5", not "1,5").
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is not null && p is not null &&
           string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), System.Convert.ToString(p, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 2 || values[0] is not long received || values[1] is not long size || size <= 0) return 0d;
        return Math.Clamp(received * 100.0 / size, 0, 100);
    }
    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InitialsBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0xF4, 0x72, 0xB6),
        Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x34, 0xD3, 0x99),
        Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0x38, 0xBD, 0xF8),
    };
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var s = value?.ToString() ?? "";
        var h = 0; foreach (var ch in s) h = unchecked(h * 31 + ch);
        var brush = new SolidColorBrush(Palette[Math.Abs(h) % Palette.Length]);
        brush.Freeze();
        return brush;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Picks the folder or the video template for mixed folder/video lists.</summary>
public sealed class EntryTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? Item { get; set; }
    public DataTemplate? Folder { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ViewModels.FolderVm or ViewModels.LocalFolderVm ? Folder : Item;
}

public sealed class WidthMinusConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is double d && double.TryParse(p?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? Math.Max(0, d - m) : 0d;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
