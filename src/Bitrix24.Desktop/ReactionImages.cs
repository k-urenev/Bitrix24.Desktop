using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bitrix24.Desktop;

internal sealed class ReactionImages
{
    public static ReactionImages Shared { get; } = new();

    private const double Slot = 28;

    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReactionGlyph> _glyphs = new(StringComparer.Ordinal);

    public ReactionGlyph Glyph(string code)
    {
        if (_glyphs.TryGetValue(code, out var cached))
            return cached;

        var source = For(code) as BitmapSource;
        if (source is null)
        {
            var empty = new ReactionGlyph(null, 0, 0);
            _glyphs[code] = empty;
            return empty;
        }

        var bounds = OpaqueBounds(source);
        var scale = Slot / source.PixelWidth;
        BitmapSource glyph = source;
        if (bounds.X != 0 || bounds.Y != 0 || bounds.Width != source.PixelWidth || bounds.Height != source.PixelHeight)
        {
            var cropped = new CroppedBitmap(source, bounds);
            cropped.Freeze();
            glyph = cropped;
        }

        var result = new ReactionGlyph(glyph, bounds.Width * scale, bounds.Height * scale);
        _glyphs[code] = result;
        return result;
    }

    private static Int32Rect OpaqueBounds(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                if (pixels[row + (x * 4) + 3] <= 16)
                    continue;
                if (x < minX)
                    minX = x;
                if (y < minY)
                    minY = y;
                if (x > maxX)
                    maxX = x;
                if (y > maxY)
                    maxY = y;
            }
        }

        if (maxX < minX)
            return new Int32Rect(0, 0, width, height);
        return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public ImageSource? For(string code)
    {
        if (_images.TryGetValue(code, out var cached))
            return cached;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reactions", code + ".png");
        if (!File.Exists(path))
        {
            _images[code] = null;
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _images[code] = image;
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
        {
            _images[code] = null;
            return null;
        }
    }
}

internal readonly record struct ReactionGlyph(ImageSource? Image, double Width, double Height);

public sealed class ReactionChoice
{
    public ReactionChoice(string code, string title, ImageSource? image)
    {
        Code = code;
        Title = title;
        Image = image;
    }

    public string Code { get; }

    public string Title { get; }

    public ImageSource? Image { get; }
}
