using System.Windows.Media;
using Bitrix24.Desktop.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bitrix24.Desktop.ViewModels;

public partial class DialogItem : ObservableObject
{
    public DialogItem(string dialogId) => DialogId = dialogId;

    public string DialogId { get; }

    public int ChatId { get; set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _timeLabel = "";
    [ObservableProperty] private int _unread;
    [ObservableProperty] private string _initials = "?";
    [ObservableProperty] private string _color = "#00ACE3";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private int _lastMessageId;
    [ObservableProperty] private int _lastAuthorId;
    [ObservableProperty] private bool _showReceipt;
    [ObservableProperty] private bool _showUnreadReaction;
    [ObservableProperty] private bool _receiptViewed;
    [ObservableProperty] private int _othersReadMessageId;
    [ObservableProperty] private string _presence = "";

    private int _userId;
    private int _unconfirmedAfter;

    public bool HasUnread => Unread > 0;

    public bool ShowListReceipt => ShowReceipt && !ShowUnreadReaction;

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public bool AvatarKnown { get; private set; }

    public bool HasListFace => _listFace is not null;

    public bool HasTalkFace => _talkFace is not null;

    public object? ListFace => _listFace;

    public object? TalkFace => _talkFace;

    private object? _listFace;
    private object? _talkFace;

    public bool IsGroup =>
        Type is "chat"
        || DialogId.StartsWith("chat", StringComparison.OrdinalIgnoreCase)
        || DialogId.StartsWith("sg", StringComparison.OrdinalIgnoreCase);

    public int ProfileUserId =>
        IsGroup || !int.TryParse(DialogId, out var id) || id <= 0 ? 0 : id;

    public bool CanOpenProfile => ProfileUserId > 0;

    public string Subtitle => IsGroup ? "Чат" : "Пользователь";

    public bool HasPresenceText => Presence.Length > 0;

    public string UnreadLabel => Unread > 99 ? "99+" : Unread.ToString();

    partial void OnShowReceiptChanged(bool value) => OnPropertyChanged(nameof(ShowListReceipt));

    partial void OnShowUnreadReactionChanged(bool value) => OnPropertyChanged(nameof(ShowListReceipt));

    partial void OnUnreadChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadLabel));
    }

    partial void OnAvatarUrlChanged(string? value) => OnPropertyChanged(nameof(HasAvatar));

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(ProfileUserId));
        OnPropertyChanged(nameof(CanOpenProfile));
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnPresenceChanged(string value) => OnPropertyChanged(nameof(HasPresenceText));

    public static DialogItem From(DialogSummary source, int userId)
    {
        var item = new DialogItem(source.DialogId);
        item.Apply(source, userId);
        return item;
    }

    public void Apply(DialogSummary source, int userId)
    {
        _userId = userId;
        if (source.ChatId > 0)
            ChatId = source.ChatId;
        Title = string.IsNullOrWhiteSpace(source.Title) ? "Диалог" : source.Title;
        Unread = source.UnreadCounter;
        Initials = TextCleaner.Initials(Title);
        Color = NormalizeColor(source.Color);
        Type = source.Type ?? "";
        if (source.AvatarKnown)
        {
            AvatarKnown = true;
            AvatarUrl = string.IsNullOrWhiteSpace(source.AvatarUrl) ? null : source.AvatarUrl;
        }
        else if (!string.IsNullOrWhiteSpace(source.AvatarUrl))
        {
            AvatarUrl = source.AvatarUrl;
        }
        if (IsGroup)
            Presence = "";
        else if (source.HasPresence)
            Presence = PresenceText.Format(source.IsOnline, source.LastSeenAt, source.Gender, DateTimeOffset.Now);
        if (LastMessageId > source.LastMessageId)
            return;

        var preview = QuoteText.Flatten(source.LastMessageText);
        Preview = preview.Length == 0 ? "Нет сообщений" : preview;
        TimeLabel = TextCleaner.ShortTime(source.LastMessageAt);
        LastMessageId = source.LastMessageId;
        LastAuthorId = source.LastMessageAuthorId;

        var read = Math.Max(OthersReadMessageId, source.OthersReadMessageId);
        var mine = userId > 0 && source.LastMessageAuthorId == userId && source.LastMessageId > 0;
        if (mine && source.LastMessageDelivered && source.LastMessageId > read)
            read = source.LastMessageId;
        OthersReadMessageId = read;

        var pending = _unconfirmedAfter > 0 && source.LastMessageId <= _unconfirmedAfter;
        if (source.LastMessageId > _unconfirmedAfter)
            _unconfirmedAfter = 0;
        ShowReceipt = pending ? userId > 0 : mine;
        ReceiptViewed = !pending && mine && read >= source.LastMessageId;
    }

    public void ShowRemote(string? url)
    {
        var face = string.IsNullOrWhiteSpace(url) ? null : url;
        SetFace(ref _listFace, face, nameof(ListFace), nameof(HasListFace));
        SetFace(ref _talkFace, face, nameof(TalkFace), nameof(HasTalkFace));
    }

    public void ShowCached(ImageSource? list, ImageSource? talk)
    {
        SetFace(ref _listFace, list, nameof(ListFace), nameof(HasListFace));
        SetFace(ref _talkFace, talk, nameof(TalkFace), nameof(HasTalkFace));
    }

    private void SetFace(ref object? field, object? value, string faceName, string hasName)
    {
        if (Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(faceName);
        OnPropertyChanged(hasName);
    }

    public void NoteOutgoing(int userId)
    {
        _userId = userId;
        if (userId <= 0)
            return;
        _unconfirmedAfter = LastMessageId;
        LastAuthorId = userId;
        ShowReceipt = true;
        ReceiptViewed = false;
    }

    public void NoteLatest(int userId, ChatMessage message)
    {
        if (message.Id < LastMessageId)
            return;
        _userId = userId;
        if (message.Id > _unconfirmedAfter)
            _unconfirmedAfter = 0;
        LastMessageId = message.Id;
        LastAuthorId = message.AuthorId;
        var mine = !message.IsSystem && message.AuthorId == userId && userId > 0;
        ShowReceipt = mine;
        ReceiptViewed = mine && OthersReadMessageId >= message.Id;
    }

    public void NoteCursor(int messageId)
    {
        OthersReadMessageId = messageId;
        if (ShowReceipt && LastMessageId > 0)
            ReceiptViewed = _unconfirmedAfter == 0 && messageId >= LastMessageId;
    }

    private static string NormalizeColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return "#00ACE3";
        return color.StartsWith('#') ? color : "#" + color;
    }
}
