using System.Windows;

namespace Bitrix24.Desktop;

internal static class WindowFit
{
    public static void Apply(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (window.MinWidth > area.Width)
            window.MinWidth = area.Width;
        if (window.MinHeight > area.Height)
            window.MinHeight = area.Height;

        var sizeToContent = window.SizeToContent;
        if (sizeToContent is SizeToContent.Manual or SizeToContent.Height)
        {
            if (!double.IsNaN(window.Width) && window.Width > area.Width)
                window.Width = area.Width;
        }

        if (sizeToContent is SizeToContent.Manual or SizeToContent.Width)
        {
            if (!double.IsNaN(window.Height) && window.Height > area.Height)
                window.Height = area.Height;
        }

        var width = Positive(window.Width) ?? Positive(window.ActualWidth) ?? Math.Min(480, area.Width);
        var height = sizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight
            ? Positive(window.ActualHeight) ?? Positive(window.Height) ?? Math.Min(640, area.Height)
            : Positive(window.Height) ?? Positive(window.ActualHeight) ?? Math.Min(640, area.Height);

        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        window.Left = area.Left + Math.Max(0, (area.Width - width) / 2);
        window.Top = area.Top + Math.Max(0, (area.Height - height) / 2);
    }

    private static double? Positive(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 ? value : null;
}
