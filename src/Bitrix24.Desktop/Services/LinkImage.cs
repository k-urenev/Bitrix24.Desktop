using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Services;

internal static class LinkImage
{
    public static ImageSource? Decode(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return null;
        }
    }
}
