using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;

namespace Bitrix24.Desktop;

internal sealed class EmojiSection
{
    public EmojiSection(string title) => Title = title;

    public string Title { get; }

    public List<EmojiEntry> Items { get; } = new();
}

internal readonly record struct EmojiEntry(int CodePoint, string Name, string Text);

internal sealed class EmojiCatalog
{
    public static EmojiCatalog Shared { get; } = Load();

    private readonly Dictionary<int, string> _files;
    private readonly Dictionary<int, ImageSource?> _images = new();
    private readonly Dictionary<int, int> _pixels = new();

    private EmojiCatalog(Dictionary<int, string> files, IReadOnlyList<EmojiSection> sections)
    {
        _files = files;
        Sections = sections;
    }

    public IReadOnlyList<EmojiSection> Sections { get; }

    public bool Has(int codePoint) => _files.ContainsKey(codePoint);

    public int PixelSize(int codePoint)
    {
        if (_pixels.TryGetValue(codePoint, out var cached))
            return cached;

        if (!_files.TryGetValue(codePoint, out var path))
            return 0;

        var size = ReadPngSide(path);
        _pixels[codePoint] = size;
        return size;
    }

    public ImageSource? ImageFor(int codePoint)
    {
        if (_images.TryGetValue(codePoint, out var cached))
            return cached;

        if (!_files.TryGetValue(codePoint, out var path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _images[codePoint] = image;
            return image;
        }
        catch
        {
            _images[codePoint] = null;
            return null;
        }
    }

    public WpfImage? Picture(int codePoint, double size)
    {
        var source = ImageFor(codePoint);
        if (source == null)
            return null;

        var image = new WpfImage
        {
            Source = source,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static int ReadPngSide(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) < header.Length)
                return 0;
            if (header[0] != 0x89 || header[1] != 0x50)
                return 0;

            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            if (width <= 0 || height <= 0)
                return 0;
            return Math.Min(width, height);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static EmojiCatalog Load()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "emoji");
        var files = new Dictionary<int, string>();
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.png"))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) && codePoint > 0)
                    files[codePoint] = path;
            }
        }

        return new EmojiCatalog(files, ReadSections(folder, files));
    }

    private static List<EmojiSection> ReadSections(string folder, Dictionary<int, string> files)
    {
        var sections = new List<EmojiSection>();
        var indexPath = Path.Combine(folder, "index.txt");
        if (!File.Exists(indexPath))
        {
            if (files.Count == 0)
                return sections;

            var all = new EmojiSection("Смайлы");
            foreach (var codePoint in files.Keys.OrderBy(code => code))
                all.Items.Add(new EmojiEntry(codePoint, "", char.ConvertFromUtf32(codePoint)));
            sections.Add(all);
            return sections;
        }

        EmojiSection? current = null;
        foreach (var line in File.ReadLines(indexPath))
        {
            if (line.Length == 0)
                continue;

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                current = new EmojiSection(line[2..]);
                sections.Add(current);
                continue;
            }

            var split = line.IndexOf('\t');
            var code = split < 0 ? line : line[..split];
            var name = split < 0 ? "" : line[(split + 1)..];
            if (!int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                continue;
            if (!files.ContainsKey(codePoint))
                continue;

            current ??= new EmojiSection("Смайлы");
            if (!sections.Contains(current))
                sections.Add(current);
            current.Items.Add(new EmojiEntry(codePoint, name, char.ConvertFromUtf32(codePoint)));
        }

        sections.RemoveAll(section => section.Items.Count == 0);
        return sections;
    }
}
