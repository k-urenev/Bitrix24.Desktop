using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Rest;
using Bitrix24.Desktop.ViewModels;

namespace Bitrix24.Desktop.Services;

internal readonly record struct DecodedPreview(ImageSource Source, int PixelWidth, int PixelHeight);

internal sealed class ImagePreviewLoader
{
    private readonly BitrixApi _api;
    private readonly Action? _changed;
    private readonly string _folder;
    private readonly ConcurrentDictionary<int, Task<DecodedPreview?>> _loads = new();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private string? _token;
    private bool _denied;

    public ImagePreviewLoader(BitrixApi api, Action? changed = null)
    {
        _api = api;
        _changed = changed;
        _folder = Path.Combine(AppPaths.Create().Directory, "previews");
        Task.Run(() =>
        {
            try
            {
                PreviewCache.Prune(_folder);
            }
            catch (Exception exception)
            {
                FileLog.Write(exception);
            }
        });
    }

    public void Queue(FileItem item, string? accessToken)
    {
        if (!string.Equals(_token, accessToken, StringComparison.Ordinal))
        {
            _token = accessToken;
            _denied = false;
        }

        if (_denied || !item.IsImage || item.Id <= 0 || item.HasPicture)
            return;

        var load = _loads.GetOrAdd(item.Id, LoadAsync);
        _ = ApplyAsync(item, load);
    }

    public async Task<ImageSource?> ViewerAsync(FileItem item)
    {
        if (!item.IsImage || item.Id <= 0)
            return item.Picture;

        try
        {
            var bytes = await BytesAsync(item.Id).ConfigureAwait(false);
            if (bytes is null)
                return null;

            var limit = ViewerFrame.Pixels;
            return DecodeViewer(bytes, limit.Width, limit.Height);
        }
        catch (BitrixException exception) when (exception.IsAccessDenied)
        {
            _denied = true;
            FileLog.Write(exception);
            return null;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return null;
        }
    }

    private async Task<byte[]?> BytesAsync(int id)
    {
        var cached = ReadCache(id);
        if (cached is not null)
            return cached;

        byte[] bytes;
        await _downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            cached = ReadCache(id);
            if (cached is not null)
                return cached;

            bytes = await _api.DownloadDiskFileAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _downloads.Release();
        }

        if (!LooksLikeImage(bytes))
            return null;

        WriteCache(id, bytes);
        return bytes;
    }

    private async Task ApplyAsync(FileItem item, Task<DecodedPreview?> load)
    {
        DecodedPreview? picture;
        try
        {
            picture = await load.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            picture = null;
        }

        if (picture is null)
        {
            _loads.TryRemove(item.Id, out _);
            return;
        }

        var decoded = picture.Value;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _loads.TryRemove(item.Id, out _);
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            item.RememberPixels(decoded.PixelWidth, decoded.PixelHeight);
            _changed?.Invoke();
            item.Picture = decoded.Source;
        });
        _loads.TryRemove(item.Id, out _);
    }

    private async Task<DecodedPreview?> LoadAsync(int id)
    {
        try
        {
            var cached = ReadCache(id);
            if (cached is not null)
                return Decode(cached);

            byte[] bytes;
            await _downloads.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await _api.DownloadDiskFileAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _downloads.Release();
            }

            if (!LooksLikeImage(bytes))
                return null;

            var picture = Decode(bytes);
            WriteCache(id, bytes);
            return picture;
        }
        catch (BitrixException exception) when (exception.IsAccessDenied)
        {
            _denied = true;
            FileLog.Write(exception);
            return null;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return null;
        }
    }

    private byte[]? ReadCache(int id)
    {
        var path = Path.Combine(_folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!LooksLikeImage(bytes))
            {
                TryDelete(path);
                return null;
            }

            try
            {
                PreviewCache.Touch(path);
            }
            catch (IOException)
            {
            }

            return bytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(int id, byte[] bytes)
    {
        PreviewCache.Store(_folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture), bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DecodedPreview Decode(byte[] bytes)
    {
        int pixelWidth;
        int pixelHeight;
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            pixelWidth = frame.PixelWidth;
            pixelHeight = frame.PixelHeight;
        }

        var (storedWidth, storedHeight) = PreviewFit.StoredPixels(pixelWidth, pixelHeight);
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (storedWidth > 0 && storedWidth < pixelWidth)
            image.DecodePixelWidth = storedWidth;
        if (storedHeight > 0 && storedHeight < pixelHeight)
            image.DecodePixelHeight = storedHeight;
        image.EndInit();
        image.Freeze();
        return new DecodedPreview(image, pixelWidth, pixelHeight);
    }

    private static ImageSource DecodeViewer(byte[] bytes, int limitWidth, int limitHeight)
    {
        int pixelWidth;
        int pixelHeight;
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            pixelWidth = frame.PixelWidth;
            pixelHeight = frame.PixelHeight;
        }

        var decodeWidth = PreviewFit.ViewerDecodeWidth(pixelWidth, pixelHeight, limitWidth, limitHeight);
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (decodeWidth > 0)
            image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 12)
            return false;
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return true;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            return true;
        if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return true;
        if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return true;
        return bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
    }
}
