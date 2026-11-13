using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.ViewModels;

public sealed class MessageItem : INotifyPropertyChanged
{
    private string _dayLabel = "";
    private bool _showAvatar;
    private double _bubbleWidth = 420;
    private double _bubbleMinWidth;
    private bool _fadeOnOpen;
    private bool _fadeOnArrive;

    private string _author = "";
    private string _text = "";
    private string _timeLabel = "";
    private string _initials = "";
    private string _color = "#00ACE3";
    private ImageSource? _avatarImage;
    private string? _authorAvatarUrl;
    private IReadOnlyList<FileItem> _files = Array.Empty<FileItem>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; init; }

    public string Author
    {
        get => _author;
        set => Set(ref _author, value, nameof(Author));
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            OnChanged(nameof(Text));
            OnChanged(nameof(HasText));
        }
    }

    public string TimeLabel
    {
        get => _timeLabel;
        set
        {
            if (_timeLabel == value)
                return;
            _timeLabel = value;
            OnChanged(nameof(TimeLabel));
            OnChanged(nameof(HasTime));
        }
    }

    public DateTimeOffset? SentAt { get; set; }
    public bool IsMine { get; init; }
    public bool IsSystem { get; init; }
    public bool ShowAuthor { get; init; }

    public string Initials
    {
        get => _initials;
        set => Set(ref _initials, value, nameof(Initials));
    }

    public string Color
    {
        get => _color;
        set => Set(ref _color, value, nameof(Color));
    }

    public int AuthorId { get; private set; }

    public string? AuthorAvatarUrl => _authorAvatarUrl;

    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        set
        {
            if (ReferenceEquals(_avatarImage, value))
                return;
            _avatarImage = value;
            OnChanged(nameof(AvatarImage));
            OnChanged(nameof(HasAvatar));
        }
    }

    public void RememberAuthor(int authorId, string? avatarUrl)
    {
        AuthorId = authorId;
        _authorAvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl;
    }

    public IReadOnlyList<FileItem> Files
    {
        get => _files;
        set
        {
            _files = value;
            OnChanged(nameof(Files));
            OnChanged(nameof(HasFiles));
        }
    }

    private IReadOnlyList<ChatReaction> _reactions = Array.Empty<ChatReaction>();
    private IReadOnlyList<ReactionChip> _chips = Array.Empty<ReactionChip>();
    private bool _showReact;

    public IReadOnlyList<ChatReaction> ReactionSource => _reactions;

    public IReadOnlyList<ReactionChip> Reactions => _chips;

    public bool HasReactions => _chips.Count > 0;

    public bool ShowReact
    {
        get => _showReact;
        set
        {
            if (IsSystem)
                value = false;
            if (_showReact == value)
                return;
            _showReact = value;
            OnChanged(nameof(ShowReact));
        }
    }

    public bool SetReactions(IReadOnlyList<ChatReaction> reactions, Func<ChatReactionFace, double, ImageSource?>? picture = null)
    {
        if (SameReactions(_reactions, reactions))
            return false;

        _reactions = reactions;
        RebuildFaces(picture);
        return true;
    }

    public void RepaintFaces(Func<ChatReactionFace, double, ImageSource?> picture)
    {
        if (_reactions.Count == 0)
            return;
        RebuildFaces(picture);
    }

    private void RebuildFaces(Func<ChatReactionFace, double, ImageSource?>? picture)
    {
        var chips = new ReactionChip[_reactions.Count];
        for (var i = 0; i < _reactions.Count; i++)
            chips[i] = new ReactionChip(Id, _reactions[i], picture);
        _chips = chips;
        OnChanged(nameof(Reactions));
        OnChanged(nameof(HasReactions));
    }

    private static bool SameReactions(IReadOnlyList<ChatReaction> current, IReadOnlyList<ChatReaction> next)
    {
        if (current.Count != next.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Code != next[i].Code || current[i].Count != next[i].Count || current[i].Mine != next[i].Mine)
                return false;
            if (current[i].Faces.Count != next[i].Faces.Count)
                return false;
            for (var face = 0; face < current[i].Faces.Count; face++)
            {
                if (current[i].Faces[face].Id != next[i].Faces[face].Id
                    || current[i].Faces[face].AvatarUrl != next[i].Faces[face].AvatarUrl)
                    return false;
            }
        }

        return true;
    }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasTime => TimeLabel.Length > 0;

    private string _linkUrl = "";
    private string _linkTitle = "";
    private string _linkDescription = "";
    private ImageSource? _linkImage;

    public string LinkUrl => _linkUrl;
    public string LinkTitle => _linkTitle;
    public string LinkDescription => _linkDescription;
    public ImageSource? LinkImage => _linkImage;
    public bool HasLinkPreview => _linkTitle.Length > 0;
    public bool HasLinkDescription => _linkDescription.Length > 0;
    public bool HasLinkImage => _linkImage is not null;

    public void ShowLink(string url, string title, string description, ImageSource? image)
    {
        _linkUrl = url;
        _linkTitle = title;
        _linkDescription = description;
        _linkImage = image;
        OnChanged(nameof(LinkUrl));
        OnChanged(nameof(LinkTitle));
        OnChanged(nameof(LinkDescription));
        OnChanged(nameof(LinkImage));
        OnChanged(nameof(HasLinkPreview));
        OnChanged(nameof(HasLinkDescription));
        OnChanged(nameof(HasLinkImage));
    }

    public void ClearLink()
    {
        if (_linkTitle.Length == 0 && _linkImage is null)
            return;
        ShowLink("", "", "", null);
    }
    public bool ShowReceipt => IsMine && !IsSystem;

    public bool ApplyDisplay(
        string author,
        string text,
        string timeLabel,
        DateTimeOffset? sentAt,
        string initials,
        string color,
        IReadOnlyList<FileItem> files,
        bool edited)
    {
        Author = author;
        Text = text;
        TimeLabel = timeLabel;
        SentAt = sentAt;
        Initials = initials;
        Color = color;
        IsEdited = edited;
        if (SameFiles(_files, files))
            return false;
        Files = files;
        return true;
    }

    private static bool SameFiles(IReadOnlyList<FileItem> current, IReadOnlyList<FileItem> next)
    {
        if (current.Count != next.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Id != next[i].Id
                || current[i].Name != next[i].Name
                || current[i].Url != next[i].Url
                || current[i].Preview != next[i].Preview
                || current[i].SizeLabel != next[i].SizeLabel)
                return false;
        }

        return true;
    }

    private void Set(ref string field, string value, string name)
    {
        if (field == value)
            return;
        field = value;
        OnChanged(name);
    }

    private bool _isEdited;
    private bool _isViewed;

    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (_isEdited == value)
                return;
            _isEdited = value;
            OnChanged(nameof(IsEdited));
        }
    }

    public bool CanEdit => IsMine && !IsSystem;

    public bool IsViewed
    {
        get => _isViewed;
        set
        {
            if (_isViewed == value)
                return;
            _isViewed = value;
            OnChanged(nameof(IsViewed));
        }
    }
    public bool HasFiles => Files.Count > 0;
    public bool HasAvatar => AvatarImage is not null;
    public bool HasDay => _dayLabel.Length > 0;

    public bool FadeOnOpen => _fadeOnOpen;

    public void MarkFade() => _fadeOnOpen = true;

    public void FinishFade()
    {
        if (!_fadeOnOpen)
            return;
        _fadeOnOpen = false;
        OnChanged(nameof(FadeOnOpen));
    }

    public bool FadeOnArrive => _fadeOnArrive;

    public void MarkArrive()
    {
        if (_fadeOnArrive)
            return;
        _fadeOnArrive = true;
        OnChanged(nameof(FadeOnArrive));
    }

    public void FinishArrive()
    {
        if (!_fadeOnArrive)
            return;
        _fadeOnArrive = false;
        OnChanged(nameof(FadeOnArrive));
    }

    public double BubbleWidth => _bubbleWidth;
    public double BubbleMinWidth => _bubbleMinWidth;

    public string DayLabel
    {
        get => _dayLabel;
        set
        {
            if (_dayLabel == value)
                return;
            _dayLabel = value;
            OnChanged(nameof(DayLabel));
            OnChanged(nameof(HasDay));
        }
    }

    public bool ShowAvatar
    {
        get => _showAvatar;
        set
        {
            if (_showAvatar == value)
                return;
            _showAvatar = value;
            OnChanged(nameof(ShowAvatar));
        }
    }

    public void ApplyPreviewFrame(double windowWidth, double windowHeight, double viewportWidth, Func<string, double> fileRow)
    {
        var chatMax = viewportWidth > 96 ? viewportWidth - 96 : 320;
        var textMax = Math.Min(420, Math.Max(160, chatMax));
        if (!HasFiles)
        {
            SetBubble(textMax, 0);
            return;
        }

        var inner = Math.Max(1, chatMax - 24);
        double imageWidth = 0;
        double rowWidth = 0;
        foreach (var file in Files)
        {
            var frame = PreviewFit.Display(file.PixelWidth, file.PixelHeight, windowWidth, windowHeight, inner);
            file.SetFrame(frame.Width, frame.Height);
            imageWidth = Math.Max(imageWidth, frame.Width);
            rowWidth = Math.Max(rowWidth, fileRow(file.Name));
        }

        var block = PreviewFit.BlockWidth(imageWidth, rowWidth, inner, 24);
        var max = imageWidth > 0
            ? Math.Min(chatMax, Math.Max(block, Math.Min(160, chatMax)))
            : Math.Min(chatMax, Math.Max(textMax, block));
        var min = Math.Min(max, PreviewFit.BlockWidth(0, rowWidth, inner, 24));
        SetBubble(max, min);
    }

    private void SetBubble(double width, double minWidth)
    {
        if (_bubbleWidth != width)
        {
            _bubbleWidth = width;
            OnChanged(nameof(BubbleWidth));
        }

        if (_bubbleMinWidth == minWidth)
            return;
        _bubbleMinWidth = minWidth;
        OnChanged(nameof(BubbleMinWidth));
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FileItem : INotifyPropertyChanged
{
    private ImageSource? _picture;
    private double _frameWidth;
    private double _frameHeight;

    public FileItem(string name, string? url, long size, string? preview, int id = 0)
    {
        Name = name;
        Url = url;
        SizeLabel = FormatSize(size);
        Preview = string.IsNullOrWhiteSpace(preview) ? null : preview;
        var resolved = id > 0 ? id : IdFromUrl(preview);
        Id = resolved > 0 ? resolved : IdFromUrl(url);
        IsImage = IsImageName(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string? Url { get; }
    public string SizeLabel { get; }
    public string? Preview { get; }
    public int Id { get; }
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public bool IsImage { get; }
    public double FrameWidth => _frameWidth;
    public double FrameHeight => _frameHeight;
    public bool HasFrame => _frameWidth > 0 && _frameHeight > 0;
    public bool HasLink => !string.IsNullOrWhiteSpace(Url);
    public bool HasSize => SizeLabel.Length > 0;
    public bool HasPicture => _picture is not null;

    public ImageSource? Picture
    {
        get => _picture;
        set
        {
            if (ReferenceEquals(_picture, value))
                return;
            _picture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPicture)));
        }
    }

    public void RememberPixels(int width, int height)
    {
        PixelWidth = width;
        PixelHeight = height;
    }

    public void SetFrame(double width, double height)
    {
        var changed = false;
        if (_frameWidth != width)
        {
            _frameWidth = width;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameWidth)));
        }

        if (_frameHeight != height)
        {
            _frameHeight = height;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameHeight)));
        }

        if (changed)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFrame)));
    }

    private static int IdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return 0;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            if (!key.Equals("fileId", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(Uri.UnescapeDataString(part[(eq + 1)..]), out var id) ? id : 0;
        }

        return 0;
    }

    private static bool IsImageName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long size)
    {
        if (size <= 0)
            return "";
        if (size < 1024)
            return size.ToString(CultureInfo.InvariantCulture) + " байт";
        if (size < 1024 * 1024)
            return FormatUnits(size / 1024d) + " Кб";
        return FormatUnits(size / 1024d / 1024d) + " МБ";
    }

    private static string FormatUnits(double value)
    {
        var text = value >= 10
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return text.Replace('.', ',');
    }
}

public sealed class PendingAttachment
{
    public PendingAttachment(string name, string path, bool temporary = false)
    {
        Name = name;
        Path = path;
        Temporary = temporary;
    }

    public string Name { get; }
    public string Path { get; }
    public bool Temporary { get; }

    public void Discard()
    {
        if (!Temporary)
            return;

        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        var folder = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }
}
