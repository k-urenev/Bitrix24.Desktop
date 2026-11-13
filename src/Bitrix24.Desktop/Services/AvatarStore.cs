using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Services;

internal sealed class AvatarStore
{
    private readonly BitrixApi _api;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _downloads = new(3, 3);
    private readonly Dictionary<(int UserId, string Name), ImageSource> _pictures = new();
    private readonly Dictionary<int, string> _metaUrl = new();
    private readonly Dictionary<int, DateTimeOffset> _savedAt = new();
    private readonly Dictionary<int, int> _tickets = new();
    private readonly HashSet<int> _busy = new();
    private readonly HashSet<int> _hydrated = new();
    private readonly HashSet<(int UserId, string Name)> _missing = new();
    private readonly HashSet<int> _tracked = new();
    private readonly HashSet<int> _blank = new();
    private readonly Dictionary<int, DateTimeOffset> _failedUntil = new();
    private int[] _faceSizes = [];
    private bool _sizesReady;

    public AvatarStore(BitrixApi api)
    {
        _api = api;
        _root = Path.Combine(AppPaths.Create().Directory, "avatars");
    }

    public event Action<int>? Ready;

    public bool IsTracked(int userId)
    {
        lock (_gate)
            return _tracked.Contains(userId);
    }

    public int[] TrackedIds()
    {
        lock (_gate)
            return _tracked.ToArray();
    }

    public ImageSource? Circle(int userId, int size) =>
        Find(userId, size.ToString(CultureInfo.InvariantCulture));

    public ImageSource? Square(int userId, int size)
    {
        if (size < 1)
            return null;
        return Find(userId, "s" + size.ToString(CultureInfo.InvariantCulture));
    }

    public void Ask(int userId, string? url, bool known, bool force)
    {
        if (userId <= 0)
            return;

        if (known && string.IsNullOrWhiteSpace(url))
        {
            lock (_gate)
            {
                if (!_blank.Add(userId))
                    return;
            }

            Clear(userId);
            return;
        }

        EnsureMeta(userId);
        var slots = Slots();
        var complete = FilesReady(userId, slots);
        string? source;
        lock (_gate)
        {
            _blank.Remove(userId);
            _tracked.Add(userId);
            source = string.IsNullOrWhiteSpace(url)
                ? (_metaUrl.TryGetValue(userId, out var saved) ? saved : null)
                : url;
            var savedAt = _savedAt.TryGetValue(userId, out var at) ? at : (DateTimeOffset?)null;
            var savedUrl = _metaUrl.TryGetValue(userId, out var meta) ? meta : null;
            var fetch = AvatarCachePolicy.NeedsFetch(savedAt, savedUrl, source, DateTimeOffset.UtcNow, force, complete);
            if (!fetch)
                return;
            if (!force
                && _failedUntil.TryGetValue(userId, out var until)
                && until > DateTimeOffset.UtcNow
                && string.Equals(source, savedUrl, StringComparison.Ordinal))
                return;
            if (_busy.Contains(userId) && !force)
                return;
        }

        if (string.IsNullOrWhiteSpace(source))
            return;

        var ticket = NextTicket(userId);
        _ = DownloadAsync(userId, source, ticket, slots);
    }

    private ImageSource? Find(int userId, string name)
    {
        EnsureMeta(userId);
        lock (_gate)
        {
            if (_pictures.TryGetValue((userId, name), out var cached))
                return cached;
            if (_missing.Contains((userId, name)))
                return null;
        }

        var path = Path.Combine(Folder(userId), name + ".png");
        if (!File.Exists(path))
        {
            lock (_gate)
                _missing.Add((userId, name));
            return null;
        }

        try
        {
            var image = AvatarFrames.FromFile(path);
            lock (_gate)
                _pictures[(userId, name)] = image;
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
        {
            FileLog.Write(exception);
            lock (_gate)
                _missing.Add((userId, name));
            return null;
        }
    }

    private bool FilesReady(int userId, IReadOnlyList<AvatarSlot> slots)
    {
        if (slots.Count == 0)
            return false;

        var folder = Folder(userId);
        if (!File.Exists(Path.Combine(folder, "meta.txt")))
            return false;

        foreach (var slot in slots)
        {
            if (!File.Exists(Path.Combine(folder, slot.Name + ".png")))
                return false;
        }

        return true;
    }

    private int NextTicket(int userId)
    {
        lock (_gate)
        {
            var ticket = _tickets.TryGetValue(userId, out var current) ? current + 1 : 1;
            _tickets[userId] = ticket;
            _busy.Add(userId);
            _failedUntil.Remove(userId);
            return ticket;
        }
    }

    private async Task DownloadAsync(int userId, string url, int ticket, IReadOnlyList<AvatarSlot> slots)
    {
        try
        {
            byte[] bytes;
            await _downloads.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await _api.DownloadPictureAsync(url, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _downloads.Release();
            }

            if (!IsCurrent(userId, ticket))
                return;
            if (!IsImage(bytes))
            {
                NoteFailure(userId);
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return;

            await dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(userId, ticket))
                    return;
                try
                {
                    var pngs = AvatarFrames.Pngs(bytes, slots);
                    WriteAll(userId, pngs, url, DateTimeOffset.UtcNow);
                    lock (_gate)
                    {
                        foreach (var pair in pngs)
                            _pictures[(userId, pair.Key)] = AvatarFrames.FromPng(pair.Value);
                        _metaUrl[userId] = url;
                        _savedAt[userId] = DateTimeOffset.UtcNow;
                        _hydrated.Add(userId);
                        _failedUntil.Remove(userId);
                        _missing.RemoveWhere(key => key.UserId == userId);
                    }
                }
                catch (Exception exception)
                {
                    NoteFailure(userId);
                    FileLog.Write(exception);
                    return;
                }

                Ready?.Invoke(userId);
            });
        }
        catch (Exception exception)
        {
            NoteFailure(userId);
            FileLog.Write(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (_tickets.TryGetValue(userId, out var current) && current == ticket)
                    _busy.Remove(userId);
            }
        }
    }

    private void NoteFailure(int userId)
    {
        lock (_gate)
            _failedUntil[userId] = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private bool IsCurrent(int userId, int ticket)
    {
        lock (_gate)
            return _tickets.TryGetValue(userId, out var current) && current == ticket;
    }

    private void EnsureMeta(int userId)
    {
        lock (_gate)
        {
            if (_hydrated.Contains(userId))
                return;
        }

        string? metaUrl = null;
        DateTimeOffset? saved = null;
        var folder = Folder(userId);
        if (Directory.Exists(folder) && TryReadMeta(folder, out var url, out var at))
        {
            metaUrl = url;
            saved = at;
        }

        lock (_gate)
        {
            if (!_hydrated.Add(userId))
                return;
            if (metaUrl is not null)
                _metaUrl[userId] = metaUrl;
            if (saved is not null)
                _savedAt[userId] = saved.Value;
        }
    }

    private void Clear(int userId)
    {
        var removed = false;
        lock (_gate)
        {
            var names = new List<string>();
            foreach (var key in _pictures.Keys)
            {
                if (key.UserId == userId)
                    names.Add(key.Name);
            }

            removed = names.Count > 0 || _metaUrl.Remove(userId) || Directory.Exists(Folder(userId));
            foreach (var name in names)
                _pictures.Remove((userId, name));
            _metaUrl.Remove(userId);
            _savedAt.Remove(userId);
            _hydrated.Remove(userId);
            _missing.RemoveWhere(key => key.UserId == userId);
            _failedUntil.Remove(userId);
            _tracked.Remove(userId);
            if (_tickets.TryGetValue(userId, out var ticket))
                _tickets[userId] = ticket + 1;
            _busy.Remove(userId);
        }

        try
        {
            if (Directory.Exists(Folder(userId)))
                Directory.Delete(Folder(userId), true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileLog.Write(exception);
        }

        if (removed)
            Ready?.Invoke(userId);
    }

    private IReadOnlyList<AvatarSlot> Slots()
    {
        EnsureFaceSizes();
        var slots = new List<AvatarSlot> { new("48", 48, true), new("42", 42, true) };
        foreach (var size in _faceSizes)
            slots.Add(new AvatarSlot("s" + size.ToString(CultureInfo.InvariantCulture), size, false));
        return slots;
    }

    private void EnsureFaceSizes()
    {
        if (_sizesReady)
            return;
        _sizesReady = true;
        var sizes = new SortedSet<int>();
        foreach (var code in ReactionCatalog.Order)
        {
            var size = (int)Math.Round(ReactionImages.Shared.Glyph(code).Height);
            if (size > 0)
                sizes.Add(size);
        }

        _faceSizes = sizes.ToArray();
    }

    private void WriteAll(int userId, IReadOnlyDictionary<string, byte[]> pngs, string url, DateTimeOffset saved)
    {
        var folder = Folder(userId);
        Directory.CreateDirectory(folder);
        foreach (var pair in pngs)
        {
            var path = Path.Combine(folder, pair.Key + ".png");
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, pair.Value);
            File.Move(temporary, path, overwrite: true);
        }

        var meta = Path.Combine(folder, "meta.txt");
        var body = url.Replace("\r", "").Replace("\n", "")
            + "\n"
            + saved.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var metaTemporary = meta + ".part";
        File.WriteAllText(metaTemporary, body);
        File.Move(metaTemporary, meta, overwrite: true);
    }

    private static bool TryReadMeta(string folder, out string url, out DateTimeOffset saved)
    {
        url = "";
        saved = default;
        var meta = Path.Combine(folder, "meta.txt");
        if (!File.Exists(meta))
            return false;

        var lines = File.ReadAllLines(meta);
        if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0]))
            return false;
        if (!long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return false;

        url = lines[0];
        saved = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private string Folder(int userId) =>
        Path.Combine(_root, userId.ToString(CultureInfo.InvariantCulture));

    private static bool IsImage(byte[] bytes)
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
