using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Controls;

sealed class ClipboardImage
{
    public ClipboardImage(string path, bool temporary)
    {
        Path = path;
        Temporary = temporary;
    }

    public string Path { get; }
    public bool Temporary { get; }
}

sealed class ClipboardPaste
{
    public static ClipboardPaste Ignore { get; } = new(false, false, Array.Empty<ClipboardImage>());
    public static ClipboardPaste Fail { get; } = new(true, true, Array.Empty<ClipboardImage>());

    private ClipboardPaste(bool handled, bool failed, IReadOnlyList<ClipboardImage> images)
    {
        Handled = handled;
        Failed = failed;
        Images = images;
    }

    public static ClipboardPaste From(IReadOnlyList<ClipboardImage> images) => new(true, false, images);

    public bool Handled { get; }
    public bool Failed { get; }
    public IReadOnlyList<ClipboardImage> Images { get; }
}

static class ClipboardImages
{
    private const string Png = "PNG";

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".jfif"
    };

    public static ClipboardPaste Take(IDataObject data, Func<string> nextName)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText, false) || data.GetDataPresent(DataFormats.Text, false))
                return ClipboardPaste.Ignore;

            if (data.GetDataPresent(DataFormats.FileDrop, false)
                && data.GetData(DataFormats.FileDrop, false) is string[] files
                && files.Length > 0)
            {
                if (!files.All(IsImageFile))
                    return ClipboardPaste.Ignore;
                return ClipboardPaste.From(files.Select(path => new ClipboardImage(path, temporary: false)).ToList());
            }

            if (!data.GetDataPresent(Png, false) && !data.GetDataPresent(DataFormats.Bitmap, true))
                return ClipboardPaste.Ignore;

            if (TrySave(data, SafeName(nextName()), out var path))
                return ClipboardPaste.From([new ClipboardImage(path, temporary: true)]);

            return ClipboardPaste.Fail;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return ClipboardPaste.Fail;
        }
    }

    private static bool IsImageFile(string path) =>
        Extensions.Contains(Path.GetExtension(path)) && File.Exists(path);

    private static string SafeName(string name)
    {
        var file = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(file))
            file = "Изображение.png";
        foreach (var bad in Path.GetInvalidFileNameChars())
            file = file.Replace(bad, '_');
        return file;
    }

    private static bool TrySave(IDataObject data, string name, out string path)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Bitrix24.Desktop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, name);
        try
        {
            if (data.GetDataPresent(Png, false) && WritePng(data.GetData(Png, false), path))
                return true;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap, true)
                && data.GetData(DataFormats.Bitmap, true) is BitmapSource source
                && source.PixelWidth > 0
                && source.PixelHeight > 0)
            {
                SaveBitmap(source, path);
                return true;
            }
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        DeleteFolder(folder);
        path = "";
        return false;
    }

    private static bool WritePng(object? data, string path)
    {
        switch (data)
        {
            case MemoryStream stream when stream.Length > 0:
                stream.Position = 0;
                using (var file = File.Create(path))
                    stream.CopyTo(file);
                return true;
            case byte[] bytes when bytes.Length > 0:
                File.WriteAllBytes(path, bytes);
                return true;
            default:
                return false;
        }
    }

    private static void SaveBitmap(BitmapSource source, string path)
    {
        BitmapSource normalized = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = normalized.PixelWidth;
        var height = normalized.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        normalized.CopyPixels(pixels, stride, 0);
        var dpiX = normalized.DpiX > 0 ? normalized.DpiX : 96;
        var dpiY = normalized.DpiY > 0 ? normalized.DpiY : 96;
        var copy = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(copy));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }
}
