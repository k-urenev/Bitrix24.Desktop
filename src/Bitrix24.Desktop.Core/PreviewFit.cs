namespace Bitrix24.Desktop.Core;

public readonly record struct FittedPreview(double Width, double Height);

public static class PreviewFit
{
    public const int StoredWidth = 1000;
    public const int StoredHeight = 500;

    public static (int Width, int Height) StoredPixels(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return (0, 0);

        var scale = Math.Min(1d, Math.Min(StoredWidth / (double)pixelWidth, StoredHeight / (double)pixelHeight));
        return (
            Math.Max(1, (int)Math.Round(pixelWidth * scale)),
            Math.Max(1, (int)Math.Round(pixelHeight * scale)));
    }

    public static FittedPreview Display(
        int pixelWidth,
        int pixelHeight,
        double windowWidth,
        double windowHeight,
        double contentMaxWidth)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || contentMaxWidth <= 0)
            return new FittedPreview(0, 0);

        var maxWidth = contentMaxWidth;
        if (windowWidth > 0)
            maxWidth = Math.Min(maxWidth, windowWidth * 0.45);
        maxWidth = Math.Min(maxWidth, StoredWidth);

        var maxHeight = (double)StoredHeight;
        if (windowHeight > 0)
            maxHeight = Math.Min(maxHeight, windowHeight * 0.80);

        if (maxWidth < 1)
            maxWidth = 1;
        if (maxHeight < 1)
            maxHeight = 1;

        var scale = Math.Min(1d, Math.Min(maxWidth / pixelWidth, maxHeight / pixelHeight));
        return new FittedPreview(pixelWidth * scale, pixelHeight * scale);
    }

    public static (int Width, int Height) ViewerBox(int screenWidth, int screenHeight, int insetWidth, int insetHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0)
            return (1, 1);

        return (
            Math.Max(1, screenWidth - Math.Max(0, insetWidth)),
            Math.Max(1, screenHeight - Math.Max(0, insetHeight)));
    }

    public static int ViewerDecodeWidth(int pixelWidth, int pixelHeight, int limitWidth, int limitHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || limitWidth <= 0 || limitHeight <= 0)
            return 0;
        if (pixelWidth <= limitWidth && pixelHeight <= limitHeight)
            return 0;

        var scale = Math.Min(limitWidth / (double)pixelWidth, limitHeight / (double)pixelHeight);
        var width = Math.Max(1, (int)Math.Floor(pixelWidth * scale));
        return width < pixelWidth ? width : 0;
    }

    public static double BlockWidth(double imageWidth, double fileRowWidth, double contentMaxWidth, double padding)
    {
        var content = Math.Max(0, imageWidth);
        if (fileRowWidth > 0 && contentMaxWidth > 0)
            content = Math.Max(content, Math.Min(fileRowWidth, contentMaxWidth));
        if (contentMaxWidth > 0)
            content = Math.Min(content, contentMaxWidth);
        return content + Math.Max(0, padding);
    }
}
