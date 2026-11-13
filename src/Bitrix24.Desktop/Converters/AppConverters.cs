using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace Bitrix24.Desktop.Converters;

public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (Invert)
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RadioBoolConverter : IValueConverter
{
    public bool WhenChecked { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag == WhenChecked;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? WhenChecked : Binding.DoNothing;
}

public sealed class HexBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = Create(MediaColor.FromRgb(0x2F, 0x80, 0xED));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return Fallback;

        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(hex);
            if (converted is MediaColor color)
            {
                var brush = Create(color);
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Create(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class SubtractConverter : IValueConverter
{
    public double Amount { get; set; }
    public double Max { get; set; } = 420;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number))
            return Math.Min(Max, Math.Max(160, number - Amount));
        return 320d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
