using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Bitrix24.Desktop.ViewModels;

internal static class PreviewMeasure
{
    public static double FileRow(string name)
    {
        var dpi = 1d;
        if (Application.Current?.MainWindow is Window window)
            dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;

        var text = new FormattedText(
            name,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            14,
            System.Windows.Media.Brushes.Black,
            dpi);
        return 36 + 8 + text.WidthIncludingTrailingWhitespace;
    }
}

public sealed class LoadOlderMarker
{
    public static LoadOlderMarker Instance { get; } = new();

    private LoadOlderMarker()
    {
    }
}
