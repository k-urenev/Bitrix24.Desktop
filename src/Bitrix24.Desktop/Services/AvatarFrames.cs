using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bitrix24.Desktop.Services;

internal readonly record struct AvatarSlot(string Name, int Size, bool Circle);

internal static class AvatarFrames
{
    public static Dictionary<string, byte[]> Pngs(byte[] encoded, IReadOnlyList<AvatarSlot> slots)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.DecodePixelWidth = 256;
        image.EndInit();
        image.Freeze();

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException("Пустая картинка аватара.");

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var result = new Dictionary<string, byte[]>(slots.Count);
        foreach (var slot in slots)
        {
            if (slot.Size < 1 || result.ContainsKey(slot.Name))
                continue;
            result[slot.Name] = Encode(Scale(pixels, width, height, slot.Size, slot.Circle));
        }

        return result;
    }

    public static ImageSource FromPng(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static ImageSource FromFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource Scale(byte[] source, int width, int height, int size, bool circle)
    {
        var side = Math.Min(width, height);
        var originX = (width - side) / 2.0;
        var originY = (height - side) / 2.0;
        var pixels = new byte[size * size * 4];
        var sample = new byte[4];
        var radius = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var cover = 1.0;
                if (circle)
                {
                    var dx = x + 0.5 - radius;
                    var dy = y + 0.5 - radius;
                    cover = radius - Math.Sqrt((dx * dx) + (dy * dy));
                    if (cover <= 0)
                        continue;
                    if (cover > 1)
                        cover = 1;
                }

                var sx = originX + ((x + 0.5) * side / size) - 0.5;
                var sy = originY + ((y + 0.5) * side / size) - 0.5;
                Sample(source, width, height, sx, sy, sample);
                var offset = ((y * size) + x) * 4;
                pixels[offset] = sample[0];
                pixels[offset + 1] = sample[1];
                pixels[offset + 2] = sample[2];
                pixels[offset + 3] = (byte)Math.Round(sample[3] * cover);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Sample(byte[] source, int width, int height, double x, double y, byte[] pixel)
    {
        if (x < 0)
            x = 0;
        if (y < 0)
            y = 0;
        if (x > width - 1)
            x = width - 1;
        if (y > height - 1)
            y = height - 1;

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var tx = x - x0;
        var ty = y - y0;
        for (var channel = 0; channel < 4; channel++)
        {
            var p00 = source[((y0 * width) + x0) * 4 + channel];
            var p10 = source[((y0 * width) + x1) * 4 + channel];
            var p01 = source[((y1 * width) + x0) * 4 + channel];
            var p11 = source[((y1 * width) + x1) * 4 + channel];
            var top = p00 + ((p10 - p00) * tx);
            var bottom = p01 + ((p11 - p01) * tx);
            pixel[channel] = (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * ty)), 0, 255);
        }
    }

    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
