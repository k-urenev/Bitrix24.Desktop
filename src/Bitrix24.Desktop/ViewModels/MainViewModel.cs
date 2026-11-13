using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Bitrix24.Desktop.Core.Polling;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Bitrix24.Desktop.Core.Rest;
using Bitrix24.Desktop.Core.Storage;
using Bitrix24.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bitrix24.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BitrixApi _api;
    private readonly LocalDatabase _database;
    private readonly SessionHolder _sessions;
    private readonly AppPaths _paths;
    private readonly ImagePreviewLoader _previews;
    private readonly AvatarStore _avatars;
    private readonly HashSet<int> _prefetch = new();
    private int _selfId;
    private string? _selfUrl;
    private bool _selfKnown;
    private ImageSource? _selfFace;
    private int _refreshFaces;
    private readonly DialogFeedMemory _memory = new();
    private int _loadVersion;
    private int _receiptTicket;
    private bool _suppressSelection;
    private string? _pendingDialogId;

    internal bool PinningFeed { get; private set; }
    internal event Action? FreezeViewport;
    internal event Action? HoldViewport;
    internal event Action? FollowTail;
    internal event Action<int, int>? PlaceOpen;
    internal event Action? EditArmed;
    private int _openGeneration;
    private int _openingUnread;
    internal int OpenGeneration => _openGeneration;
    private readonly WritingTrack _writing = new();
    private MessageItem? _editing;
    private string _draft = "";
    private (string DialogId, int AfterId)? _outgoingLink;
    private readonly Dictionary<string, Dictionary<int, LinkPreview>> _linkPreviews = new();
    private readonly Dictionary<(string DialogId, int MessageId), string> _linkFetch = new();
    private readonly Dictionary<int, DateTimeOffset> _typingUntil = new();
    private DispatcherTimer? _typingTimer;
    private string? _typingDialogId;
    public bool HasTyping { get; private set; }

    public MainViewModel(BitrixApi api, LocalDatabase database, SessionHolder sessions, AppPaths paths)
    {
        _api = api;
        _database = database;
        _sessions = sessions;
        _paths = paths;
        _previews = new ImagePreviewLoader(api, FitPreviews);
        _avatars = new AvatarStore(api);
        _avatars.Ready += OnAvatarReady;
        _selfId = sessions.Current.UserId;
        ChatRows.Add(LoadOlderMarker.Instance);
        Messages.CollectionChanged += OnMessageRowsChanged;
        UserName = sessions.Current.UserName;
        PendingFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingFiles));
            SendCommand.NotifyCanExecuteChanged();
        };
        DialogsView = CollectionViewSource.GetDefaultView(Dialogs);
        DialogsView.Filter = FilterDialog;
        TaskNotices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTaskNotices));
        _launchWithWindows = WindowsStartup.IsEnabled();
        _startInTray = sessions.Current.StartInTray;
        _sendOnEnter = sessions.Current.SendOnEnter;
        var sounds = sessions.Current;
        SoundChoices.Add(new NoticeSoundChoice(NoticeSound.Incoming, NoticeCue.Incoming, "Звук при новом сообщении", sounds.SoundIncoming));
        SoundChoices.Add(new NoticeSoundChoice(NoticeSound.OpenChat, NoticeCue.OpenChat, "Звук при сообщении в открытом чате", sounds.SoundOpenChat));
        SoundChoices.Add(new NoticeSoundChoice(NoticeSound.Send, NoticeCue.Send, "Звук при отправке", sounds.SoundSend));
        SoundChoices.Add(new NoticeSoundChoice(NoticeSound.Reminder, NoticeCue.Reminder, "Звук при задаче", sounds.SoundReminder));
        NoticeSounds.Apply(sounds);
        _ = WarmSelfAsync(false);
    }

    public ObservableCollection<NoticeSoundChoice> SoundChoices { get; } = new();

    public void UseNoticeSound(NoticeSound kind, string path)
    {
        _sessions.SetNoticeSound(kind, path);
        var session = _sessions.Current;
        NoticeSounds.Apply(session);
        var choice = SoundChoices.First(item => item.Kind == kind);
        choice.FullPath = path;
        NoticeSounds.Play(choice.Cue);
    }

    public ICollectionView DialogsView { get; }

    public bool HasSearch => !string.IsNullOrWhiteSpace(Search);
    public bool HasComposer => !string.IsNullOrWhiteSpace(Composer);
    public string SendIcon => IsEditing ? "\uE73E" : "\uE724";
    public string SendTip => IsEditing ? "Сохранить" : "Отправить";
    public string SendName => IsEditing ? "Сохранить" : "Отправить";

    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        DialogsView.Refresh();
    }

    partial void OnComposerChanged(string value)
    {
        OnPropertyChanged(nameof(HasComposer));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(SendIcon));
        OnPropertyChanged(nameof(SendTip));
        OnPropertyChanged(nameof(SendName));
        SendCommand.NotifyCanExecuteChanged();
    }

    private double _windowWidth;
    private double _windowHeight;
    private double _chatWidth;
    private int _fadeGeneration;

    public void NoteChatLayout(double windowWidth, double windowHeight, double chatWidth)
    {
        if (windowWidth <= 1 || chatWidth <= 1)
            return;

        _windowWidth = windowWidth;
        _windowHeight = windowHeight;
        _chatWidth = chatWidth;
        FitPreviews();
    }

    private void FitPreviews()
    {
        if (_chatWidth <= 1)
            return;

        foreach (var message in Messages)
            FitMessage(message);
    }

    private void FitMessage(MessageItem message)
    {
        if (_chatWidth <= 1)
            return;

        message.ApplyPreviewFrame(_windowWidth, _windowHeight, _chatWidth, PreviewMeasure.FileRow);
    }

    private void OnMessageRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                ChatRows.Clear();
                ChatRows.Add(LoadOlderMarker.Instance);
                foreach (var message in Messages)
                    ChatRows.Add(message);
                break;
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                var index = e.NewStartingIndex + 1;
                for (var i = 0; i < e.NewItems.Count; i++)
                    ChatRows.Insert(index + i, e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems)
                {
                    if (item is not null)
                        ChatRows.Remove(item);
                }
                break;
        }
    }

    private bool FilterDialog(object item)
    {
        if (item is not DialogItem dialog)
            return false;
        var query = Search.Trim();
        if (query.Length == 0)
            return true;
        return dialog.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || dialog.Preview.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<DialogItem> Dialogs { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();
    public ObservableCollection<object> ChatRows { get; } = new();
    public string PortalHost => _sessions.Current.PortalDomain;

    public Task<System.Windows.Media.ImageSource?> PictureForViewerAsync(FileItem file) =>
        _previews.ViewerAsync(file);
    public ObservableCollection<PendingAttachment> PendingFiles { get; } = new();
    public bool HasPendingFiles => PendingFiles.Count > 0;
    public ObservableCollection<TaskNoticeItem> TaskNotices { get; } = new();
    public bool HasTaskNotices => TaskNotices.Count > 0;
    public int UnreadNoticeCount { get; private set; }
    public bool HasUnreadNotices => UnreadNoticeCount > 0;
    public string UnreadNoticeLabel => UnreadNoticeCount > 99 ? "99+" : UnreadNoticeCount.ToString();
    private bool _noticesOpen;

    public void SetNoticesOpen(bool open)
    {
        if (_noticesOpen == open)
            return;
        _noticesOpen = open;
        if (open)
            _database.MarkTaskNoticesRead(_sessions.Current.UserId);
        PublishNoticeUnread();
    }

    public void LoadTaskNotices()
    {
        var userId = _sessions.Current.UserId;
        if (_noticesOpen)
            _database.MarkTaskNoticesRead(userId);
        TaskNotices.Clear();
        foreach (var notice in _database.GetTaskNotices(userId))
            TaskNotices.Add(TaskNoticeItem.Create(notice.Title, notice.Body, notice.Url, notice.TaskId, notice.At));
        PublishNoticeUnread();
    }

    public void AddTaskNotices(IReadOnlyList<AppNotification> notifications)
    {
        var fresh = notifications.Where(item => item.Kind == NotificationKind.Task).ToArray();
        var at = DateTimeOffset.Now;
        for (var i = fresh.Length - 1; i >= 0; i--)
        {
            var item = fresh[i];
            var taskId = item.TaskId ?? 0;
            var card = TaskNoticeItem.Create(item.Title, item.Body, item.ExternalUrl, taskId, at);
            var existing = item.UpdateExisting
                ? TaskNotices.FirstOrDefault(notice => notice.TaskId == taskId)
                : null;
            if (existing is null)
                TaskNotices.Insert(0, card);
            else
                TaskNotices[TaskNotices.IndexOf(existing)] = card;
        }

        if (fresh.Length == 0)
            return;
        if (_noticesOpen)
            _database.MarkTaskNoticesRead(_sessions.Current.UserId);
        PublishNoticeUnread();
    }

    private void PublishNoticeUnread()
    {
        var total = _database.CountUnreadTaskNotices(_sessions.Current.UserId);
        if (total == UnreadNoticeCount)
            return;
        UnreadNoticeCount = total;
        OnPropertyChanged(nameof(UnreadNoticeCount));
        OnPropertyChanged(nameof(HasUnreadNotices));
        OnPropertyChanged(nameof(UnreadNoticeLabel));
    }

    [ObservableProperty] private DialogItem? _selectedDialog;
    [ObservableProperty] private string _composer = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editPreview = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _status = "";

    public ImageSource? SelfFace
    {
        get => _selfFace;
        private set
        {
            if (ReferenceEquals(_selfFace, value))
                return;
            _selfFace = value;
            OnPropertyChanged(nameof(SelfFace));
            OnPropertyChanged(nameof(HasSelfFace));
        }
    }

    public bool HasSelfFace => _selfFace is not null;

    public string SelfInitials => TextCleaner.Initials(SelfName());

    public string SelfColor => AuthorColor(SelfName());

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(SelfInitials));
        OnPropertyChanged(nameof(SelfColor));
        NoteSelf();
    }
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _canLoadOlder;
    [ObservableProperty] private bool _hasDialogs;
    [ObservableProperty] private bool _launchWithWindows;
    [ObservableProperty] private string _launchWithWindowsNote = "";
    [ObservableProperty] private bool _startInTray;
    [ObservableProperty] private bool _sendOnEnter;
    [ObservableProperty] private string _supportArchiveNote = "";

    public string AppVersion => global::Bitrix24.Desktop.AppRelease.Version;

    public void PackSupport(string zipPath, string window)
    {
        SupportArchiveNote = "";
        try
        {
            var selected = SelectedDialog;
            var feed = NewestShownId();
            var listed = selected?.LastMessageId ?? 0;
            var session = _sessions.Current;
            var info = string.Join(
                Environment.NewLine,
                "версия " + AppVersion,
                "время " + DateTimeOffset.Now.ToString("o"),
                "ОС " + Environment.OSVersion,
                "разряд " + (Environment.Is64BitOperatingSystem ? "64" : "32"),
                "пользователь " + session.UserId + " " + session.UserName,
                "портал " + session.PortalDomain,
                "статус " + Status,
                "окно " + window,
                "выбран " + FeedTrace.Selected(selected?.DialogId, selected?.Title),
                "лента сообщений " + Messages.Count + " хвост " + feed,
                "список последнее " + listed,
                "не беспокоить " + (session.DoNotDisturb ? "да" : "нет"),
                "запуск свёрнутым " + (session.StartInTray ? "да" : "нет"));
            SupportArchive.Write(zipPath, info, _paths.LogFile, _database);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            SupportArchiveNote = "Не удалось собрать архив.";
        }
    }

    private bool _applyingLaunchWithWindows;
    private bool _applyingStartInTray;
    private bool _applyingSendOnEnter;

    partial void OnSendOnEnterChanged(bool value)
    {
        if (_applyingSendOnEnter)
            return;

        try
        {
            _sessions.SetSendOnEnter(value);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            _applyingSendOnEnter = true;
            SendOnEnter = _sessions.Current.SendOnEnter;
            _applyingSendOnEnter = false;
        }
    }

    partial void OnStartInTrayChanged(bool value)
    {
        if (_applyingStartInTray)
            return;

        try
        {
            _sessions.SetStartInTray(value);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            _applyingStartInTray = true;
            StartInTray = _sessions.Current.StartInTray;
            _applyingStartInTray = false;
        }
    }

    partial void OnLaunchWithWindowsChanged(bool value)
    {
        if (_applyingLaunchWithWindows)
            return;

        try
        {
            WindowsStartup.SetEnabled(value);
            LaunchWithWindowsNote = "";
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            _applyingLaunchWithWindows = true;
            LaunchWithWindows = WindowsStartup.IsEnabled();
            _applyingLaunchWithWindows = false;
            LaunchWithWindowsNote = "Не удалось изменить автозапуск.";
        }
    }

    public bool HasSelection => SelectedDialog is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public void ClearForSignOut()
    {
        _suppressSelection = true;
        try
        {
            SelectedDialog = null;
            Messages.Clear();
            Dialogs.Clear();
            HasDialogs = false;
            Composer = "";
            Error = null;
            foreach (var file in PendingFiles)
                file.Discard();
            UserName = "";
            Status = "";
            SelfFace = null;
            CanLoadOlder = false;
            PendingFiles.Clear();
            TaskNotices.Clear();
            PublishUnread();
            if (UnreadNoticeCount != 0)
            {
                UnreadNoticeCount = 0;
                OnPropertyChanged(nameof(UnreadNoticeCount));
                OnPropertyChanged(nameof(HasUnreadNotices));
                OnPropertyChanged(nameof(UnreadNoticeLabel));
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    public void AddPendingFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            if (info.Length > BitrixApi.MaxAttachmentBytes)
            {
                Error = $"Файл «{info.Name}» больше 20 МБ.";
                continue;
            }

            if (PendingFiles.Any(file => string.Equals(file.Path, info.FullName, StringComparison.OrdinalIgnoreCase)))
                continue;
            PendingFiles.Add(new PendingAttachment(info.Name, info.FullName));
        }
    }

    public string NextClipboardImageName()
    {
        var used = new HashSet<string>(PendingFiles.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
        var name = "Изображение.png";
        for (var index = 2; used.Contains(name); index++)
            name = $"Изображение ({index}).png";
        return name;
    }

    public void AddClipboardImage(string path, bool temporary)
    {
        if (!temporary)
        {
            AddPendingFiles([path]);
            return;
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            new PendingAttachment(info.Name, path, temporary: true).Discard();
            Error = "Не удалось вставить изображение.";
            return;
        }

        if (info.Length > BitrixApi.MaxAttachmentBytes)
        {
            Error = $"Файл «{info.Name}» больше 20 МБ.";
            new PendingAttachment(info.Name, info.FullName, temporary: true).Discard();
            return;
        }

        if (PendingFiles.Any(file => string.Equals(file.Path, info.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            new PendingAttachment(info.Name, info.FullName, temporary: true).Discard();
            return;
        }

        PendingFiles.Add(new PendingAttachment(info.Name, info.FullName, temporary: true));
    }

    [RelayCommand]
    private void RemovePending(PendingAttachment? file)
    {
        if (file is not null && PendingFiles.Remove(file))
            file.Discard();
    }

    partial void OnSelectedDialogChanged(DialogItem? value)
    {
        CloseEdit(restoreDraft: true);
        OnPropertyChanged(nameof(HasSelection));
        SendCommand.NotifyCanExecuteChanged();
        LoadOlderCommand.NotifyCanExecuteChanged();
        if (!string.Equals(_typingDialogId, value?.DialogId, StringComparison.Ordinal))
        {
            _typingDialogId = value?.DialogId;
            _typingUntil.Clear();
            PublishTyping(DateTimeOffset.Now);
        }

        if (_suppressSelection)
            return;
        _ = LoadDialogAsync(value);
    }

    public void NoteWriting(WritingSignal signal)
    {
        if (signal.UserId <= 0 || signal.UserId == _sessions.Current.UserId)
            return;

        var now = DateTimeOffset.Now;
        var hold = _writing.HoldAfter(signal.UserId, now);
        var dialog = SelectedDialog;
        if (dialog is null || !WritingTrack.Belongs(dialog.DialogId, dialog.ChatId, dialog.IsGroup, _sessions.Current.UserId, signal.UserId, signal.DialogId))
            return;

        _typingUntil[signal.UserId] = now + hold;
        _typingDialogId = dialog.DialogId;
        PublishTyping(now);
    }

    private void PublishTyping(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var pair in _typingUntil.ToList())
        {
            if (pair.Value <= now)
            {
                _typingUntil.Remove(pair.Key);
                continue;
            }

            if (next is null || pair.Value < next)
                next = pair.Value;
        }

        var alive = _typingUntil.Count > 0;
        if (HasTyping != alive)
        {
            HasTyping = alive;
            OnPropertyChanged(nameof(HasTyping));
        }

        if (next is null)
        {
            _typingTimer?.Stop();
            return;
        }

        _typingTimer ??= new DispatcherTimer();
        _typingTimer.Tick -= OnTypingTick;
        _typingTimer.Tick += OnTypingTick;
        var delay = next.Value - now;
        if (delay < TimeSpan.FromMilliseconds(200))
            delay = TimeSpan.FromMilliseconds(200);
        _typingTimer.Stop();
        _typingTimer.Interval = delay;
        _typingTimer.Start();
    }

    private void OnTypingTick(object? sender, EventArgs e) => PublishTyping(DateTimeOffset.Now);

    partial void OnIsSendingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => LoadOlderCommand.NotifyCanExecuteChanged();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnCanLoadOlderChanged(bool value) => LoadOlderCommand.NotifyCanExecuteChanged();

    public void ApplyDialogs(IReadOnlyList<DialogSummary> dialogs)
    {
        var selectedId = SelectedDialog?.DialogId ?? _pendingDialogId;
        _suppressSelection = true;
        try
        {
            var existing = Dialogs.ToDictionary(item => item.DialogId, StringComparer.Ordinal);
            var ordered = new List<DialogItem>(dialogs.Count);
            var userId = _sessions.Current.UserId;
            foreach (var source in dialogs)
            {
                if (!existing.TryGetValue(source.DialogId, out var item))
                    item = DialogItem.From(source, userId);
                else
                    item.Apply(source, userId);
                item.ShowUnreadReaction = _database.HasUnreadReaction(item.DialogId, item.ChatId);
                ordered.Add(item);
            }

            Dialogs.Clear();
            foreach (var item in ordered)
                Dialogs.Add(item);
            HasDialogs = Dialogs.Count > 0;
            SelectedDialog = selectedId is null ? null : Dialogs.FirstOrDefault(item => item.DialogId == selectedId);
            PublishUnread();
        }
        finally
        {
            _suppressSelection = false;
        }

        if (_pendingDialogId is not null && SelectedDialog?.DialogId == _pendingDialogId)
        {
            var pending = SelectedDialog;
            _pendingDialogId = null;
            _ = LoadDialogAsync(pending);
        }

        if (SelectedDialog is not null)
            RequestReceipt(SelectedDialog);
        ApplyReceipts();
        _memory.Warm(dialogs.Select(dialog => dialog.DialogId), _database.GetMessages);
        SyncAvatars(false);
        NoteSelectedGap("после списка");
    }

    public void ApplyReactionMarks()
    {
        foreach (var item in Dialogs)
            item.ShowUnreadReaction = _database.HasUnreadReaction(item.DialogId, item.ChatId);
    }

    public void ClearSearch()
    {
        if (Search.Length == 0)
            return;

        var keep = SelectedDialog;
        _suppressSelection = true;
        try
        {
            Search = "";
            if (keep is not null)
                SelectedDialog = keep;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    public void SelectDialog(string dialogId)
    {
        ClearSearch();

        var dialog = Dialogs.FirstOrDefault(item => item.DialogId == dialogId);
        if (dialog is null)
        {
            _pendingDialogId = dialogId;
            return;
        }

        _pendingDialogId = null;
        if (!ReferenceEquals(SelectedDialog, dialog))
            SelectedDialog = dialog;
        else
            _ = LoadDialogAsync(dialog);
    }

    public async Task RefreshOpenAsync(string dialogId)
    {
        if (SelectedDialog?.DialogId != dialogId)
        {
            FileLog.Write("лента " + dialogId + " догрузку пропустили, открыт " + (SelectedDialog?.DialogId ?? "нет"));
            return;
        }

        await AppendTailAsync(dialogId, _loadVersion);
    }

    public void ReloadMessages()
    {
        var token = ++_refreshFaces;
        SyncAvatars(true);
        _ = WarmSelfAsync(true);
        if (SelectedDialog is not null)
            _ = LoadDialogAsync(SelectedDialog, token);
        else if (_refreshFaces == token)
            _refreshFaces = 0;
    }

    public bool InsertQuote(MessageItem? message, string? body)
    {
        if (message is null || SelectedDialog is null || string.IsNullOrWhiteSpace(body))
            return false;

        var block = QuoteText.Compose(
            message.Author,
            message.SentAt,
            SelectedDialog.DialogId,
            _sessions.Current.UserId,
            message.Id,
            body);
        var existing = (Composer ?? "").TrimStart('\r', '\n');
        Composer = existing.Length == 0 ? block : block + existing;
        return true;
    }

    [RelayCommand]
    private void BeginEdit(MessageItem? message)
    {
        if (message is null || !message.CanEdit || SelectedDialog is null)
            return;

        if (_editing is null)
            _draft = Composer;
        _editing = message;
        EditPreview = EditLine(message);
        IsEditing = true;
        Composer = message.Text;
        EditArmed?.Invoke();
    }

    public void EditLatest()
    {
        if (SelectedDialog is null || IsSending)
            return;

        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (!Messages[index].CanEdit)
                continue;
            BeginEdit(Messages[index]);
            return;
        }
    }

    [RelayCommand]
    private void CancelEdit() => CloseEdit(restoreDraft: true);

    private void CloseEdit(bool restoreDraft)
    {
        if (_editing is null)
            return;

        _editing = null;
        var draft = _draft;
        _draft = "";
        IsEditing = false;
        EditPreview = "";
        if (restoreDraft)
            Composer = draft;
    }

    private static string EditLine(MessageItem message)
    {
        var text = message.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        if (text.Length > 0)
            return text;
        return message.Files.Count > 0 ? message.Files[0].Name : "";
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_editing is not null)
        {
            await SaveEditAsync();
            return;
        }

        var dialog = SelectedDialog;
        if (dialog is null)
            return;

        var text = Composer.Trim();
        var files = PendingFiles.ToList();
        if (text.Length == 0 && files.Count == 0)
            return;

        var version = _loadVersion;
        var linkAfter = Messages.Count == 0 ? 0 : Messages.Max(item => item.Id);
        Composer = "";
        PendingFiles.Clear();
        Error = null;
        IsSending = true;
        if (!_sessions.Current.DoNotDisturb)
            NoticeSounds.Play(NoticeCue.Send);
        var sentFiles = 0;
        var watchLink = LinkText.FirstTarget(text) is not null;
        if (watchLink)
            _outgoingLink = (dialog.DialogId, linkAfter);
        try
        {
            if (files.Count == 0)
                await _api.SendMessageAsync(dialog.DialogId, text, CancellationToken.None);
            else
            {
                for (var index = 0; index < files.Count; index++)
                {
                    await _api.SendFileAsync(dialog.DialogId, files[index].Path, index == 0 ? text : "", CancellationToken.None);
                    sentFiles++;
                }
            }

            if (version == _loadVersion && SelectedDialog?.DialogId == dialog.DialogId)
            {
                dialog.Preview = files.Count == 0
                    ? QuoteText.Flatten(TextCleaner.ToPlain(text, singleLine: true, _sessions.Current.PortalDomain))
                    : text.Length > 0
                        ? QuoteText.Flatten(TextCleaner.ToPlain(text, singleLine: true, _sessions.Current.PortalDomain))
                        : files[0].Name;
                dialog.TimeLabel = TextCleaner.ShortTime(DateTimeOffset.Now);
                dialog.NoteOutgoing(_sessions.Current.UserId);
                await AppendTailAsync(dialog.DialogId, version, revealMine: true);
            }
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            if (watchLink
                && _outgoingLink is { } pending
                && pending.DialogId == dialog.DialogId
                && pending.AfterId == linkAfter)
                _outgoingLink = null;
            if (string.IsNullOrWhiteSpace(Composer))
                Composer = text;
            foreach (var file in files.Skip(sentFiles))
            {
                if (!PendingFiles.Any(item => string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    PendingFiles.Add(file);
            }

            Error = exception.Message;
        }
        finally
        {
            for (var index = 0; index < sentFiles; index++)
                files[index].Discard();
            IsSending = false;
        }
    }

    private async Task SaveEditAsync()
    {
        var message = _editing;
        var dialog = SelectedDialog;
        if (message is null || dialog is null)
            return;

        var text = Composer.Trim();
        if (text.Length == 0 && !message.HasFiles)
            return;

        if (text == message.Text)
        {
            CloseEdit(restoreDraft: true);
            return;
        }

        var version = _loadVersion;
        IsSending = true;
        Error = null;
        try
        {
            await _api.UpdateMessageAsync(message.Id, text, CancellationToken.None);
            _database.UpdateMessageText(dialog.DialogId, message.Id, text);
            _memory.Rewrite(dialog.DialogId, message.Id, text);
            if (version == _loadVersion && ReferenceEquals(SelectedDialog, dialog))
            {
                message.Text = text;
                message.IsEdited = true;
                NoteEditedLink(dialog.DialogId, message, text);
            }

            if (dialog.LastMessageId == message.Id)
            {
                dialog.Preview = text.Length == 0
                    ? message.Files[0].Name
                    : QuoteText.Flatten(TextCleaner.ToPlain(text, singleLine: true, _sessions.Current.PortalDomain));
            }

            CloseEdit(restoreDraft: true);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            Error = exception.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    private bool CanSend() =>
        SelectedDialog is not null
        && !IsSending
        && (_editing is not null
            ? _editing.HasFiles || !string.IsNullOrWhiteSpace(Composer)
            : !string.IsNullOrWhiteSpace(Composer) || PendingFiles.Count > 0);

    [RelayCommand(CanExecute = nameof(CanLoadOlderNow))]
    private async Task LoadOlderAsync()
    {
        var dialog = SelectedDialog;
        if (dialog is null || Messages.Count == 0)
            return;

        var version = _loadVersion;
        var oldest = Messages.Min(item => item.Id);
        IsBusy = true;
        Error = null;
        try
        {
            var older = await _api.GetMessagesAsync(dialog.DialogId, firstId: null, lastId: oldest, CancellationToken.None);
            if (version != _loadVersion || SelectedDialog?.DialogId != dialog.DialogId)
                return;

            NoteChatId(dialog, null, older);
            if (dialog.ChatId > 0)
            {
                try
                {
                    var page = await _api.GetOlderReactionsAsync(dialog.ChatId, oldest, FeedDiff.PageCount, CancellationToken.None);
                    older = Stamp(older, page);
                }
                catch (Exception exception)
                {
                    FileLog.Write(exception);
                }
            }

            if (version != _loadVersion || SelectedDialog?.DialogId != dialog.DialogId)
                return;

            _database.SaveMessages(dialog.DialogId, older);
            for (var index = older.Count - 1; index >= 0; index--)
            {
                if (Messages.Any(item => item.Id == older[index].Id))
                    continue;
                Messages.Insert(0, ToItem(older[index], fade: false));
            }

            RestyleMessages();
            ApplyReceipts();
            FitPreviews();
            CanLoadOlder = older.Count >= 50;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            Error = exception.Message;
        }
        finally
        {
            if (version == _loadVersion)
                IsBusy = false;
        }
    }

    private bool CanLoadOlderNow() => CanLoadOlder && SelectedDialog is not null && !IsBusy;

    private async Task LoadDialogAsync(DialogItem? dialog, int faceToken = 0)
    {
        try
        {
        await LoadDialogCoreAsync(dialog);
        }
        finally
        {
            if (faceToken != 0 && _refreshFaces == faceToken)
                _refreshFaces = 0;
        }
    }

    private async Task LoadDialogCoreAsync(DialogItem? dialog)
    {
        var generation = ++_fadeGeneration;
        try
        {
            await LoadDialogBodyAsync(dialog);
        }
        finally
        {
            if (generation == _fadeGeneration)
                _fadeGeneration = 0;
        }
    }

    private async Task LoadDialogBodyAsync(DialogItem? dialog)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        CanLoadOlder = false;
        Error = null;
        if (IsBusy)
            IsBusy = false;
        if (dialog is null)
        {
            Messages.Clear();
            return;
        }

        TouchOpenDialog(dialog);
        var dialogId = dialog.DialogId;
        _openGeneration++;
        _openingUnread = Math.Max(0, dialog.Unread);
        var network = _api.GetMessagesAsync(dialogId, firstId: null, lastId: null, CancellationToken.None);
        var reactions = FetchReactionsAsync(dialogId);
        var page = await OpenCachedAsync(dialogId, version, network);
        if (page is null)
            return;

        ApplyIncoming(page, dropMissing: false, keepBelowId: 0);
        RaisePlace(version);
        var floor = OldestShownId();
        var fresh = await ReadFreshAsync(network, version);
        if (fresh is null)
        {
            FileLog.Write(FeedTrace.LoadLine(dialogId, page.Count, 0, NewestShownId(), dialog.LastMessageId));
            NoteSelectedGap("после открытия");
            return;
        }

        var reactionPage = await reactions;
        if (version != _loadVersion)
            return;

        NoteChatId(dialog, reactionPage, fresh);
        if (reactionPage is not null)
            fresh = Stamp(fresh, reactionPage);
        _memory.RememberPage(dialogId, fresh);
        SaveQuiet(dialogId, fresh);
        ApplyIncoming(fresh, dropMissing: true, floor);
        RaisePlace(version);
        FileLog.Write(FeedTrace.LoadLine(dialogId, page.Count, fresh.Count, NewestShownId(), dialog.LastMessageId));
        NoteSelectedGap("после открытия");
        CanLoadOlder = fresh.Count >= FeedDiff.PageCount;
        await MarkReadAsync(dialog, fresh);
        if (version == _loadVersion && SelectedDialog?.DialogId == dialogId)
        {
            ClearUnreadReaction(dialog);
            RequestReceipt(dialog);
        }
    }

    private void ClearUnreadReaction(DialogItem dialog)
    {
        if (!dialog.ShowUnreadReaction && !_database.HasUnreadReaction(dialog.DialogId, dialog.ChatId))
            return;

        _database.ForgetChatReactions(dialog.DialogId, dialog.ChatId);
        dialog.ShowUnreadReaction = false;
        if (dialog.ChatId <= 0)
            return;

        _ = ReadAnchorsQuietAsync(dialog.ChatId);
    }

    private async Task ReadAnchorsQuietAsync(int chatId)
    {
        try
        {
            await _api.ReadReactionAnchorsAsync(chatId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private async Task<List<ChatMessage>?> OpenCachedAsync(string dialogId, int version, Task network)
    {
        if (_memory.Try(dialogId) is { Count: > 0 } remembered)
        {
            ShowInstant(FeedDiff.Instant(remembered));
            await PaintAsync();
            if (version != _loadVersion)
            {
                Forget(network);
                return null;
            }

            var page = remembered.Count >= FeedDiff.PageCount
                ? remembered
                : await StoredPageAsync(dialogId, remembered, version);
            if (page is null)
                Forget(network);
            return page;
        }

        Messages.Clear();
        var stored = await StoredPageAsync(dialogId, null, version);
        if (stored is null)
        {
            Forget(network);
            return null;
        }

        ShowInstant(FeedDiff.Instant(stored));
        await PaintAsync();
        if (version != _loadVersion)
        {
            Forget(network);
            return null;
        }

        return stored;
    }

    private async Task<IReadOnlyList<ChatMessage>?> ReadFreshAsync(Task<IReadOnlyList<ChatMessage>> network, int version)
    {
        try
        {
            var fresh = await network;
            return version == _loadVersion ? fresh : null;
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            if (version == _loadVersion)
                Error = exception.Message;
            return null;
        }
    }

    private async Task<List<ChatMessage>?> StoredPageAsync(
        string dialogId,
        List<ChatMessage>? remembered,
        int version)
    {
        IReadOnlyList<ChatMessage> stored;
        try
        {
            stored = await Task.Run(() => _database.GetMessages(dialogId));
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            stored = Array.Empty<ChatMessage>();
        }

        if (version != _loadVersion)
            return null;

        return _memory.Fold(dialogId, FeedDiff.Page(stored), remembered);
    }

    private void ShowInstant(IReadOnlyList<ChatMessage> messages)
    {
        Messages.Clear();
        foreach (var message in messages)
            Messages.Add(ToItem(message));
        RefreshShown();
        PlaceOpen?.Invoke(_openGeneration, _openingUnread);
    }

    private void RaisePlace(int version)
    {
        if (version != _loadVersion)
            return;
        PlaceOpen?.Invoke(_openGeneration, _openingUnread);
    }

    private void ApplyIncoming(IReadOnlyList<ChatMessage> incoming, bool dropMissing, int keepBelowId)
    {
        if (SelectedDialog is { } dialog)
            CaptureOutgoing(dialog.DialogId, incoming);
        RefreshExisting(incoming);
        var ids = new int[Messages.Count];
        for (var i = 0; i < Messages.Count; i++)
            ids[i] = Messages[i].Id;
        PlacePlan(FeedDiff.Plan(ids, incoming, dropMissing, keepBelowId));
    }

    private void RefreshExisting(IReadOnlyList<ChatMessage> incoming)
    {
        var token = _sessions.Current.AccessToken;
        foreach (var message in incoming)
        {
            var index = IndexOfMessage(message.Id);
            if (index < 0)
                continue;
            var item = Messages[index];
            item.RememberAuthor(message.AuthorId, AvatarFor(message));
            if (message.Reactions is not null)
                item.SetReactions(message.Reactions, FaceDuringReload);
            ApplyMessageFace(item, _refreshFaces != 0);
            var filesChanged = item.ApplyDisplay(
                    message.AuthorName,
                    message.Text,
                    TextCleaner.MessageTime(message.SentAt),
                    message.SentAt,
                    TextCleaner.Initials(message.AuthorName),
                    AuthorColor(message.AuthorName),
                    FileItems(message),
                    message.IsEdited);
            FitMessage(item);
            if (!filesChanged)
                continue;

            foreach (var file in Messages[index].Files)
                _previews.Queue(file, token);
        }
    }

    private void PlacePlan(FeedDiff.Result plan)
    {
        if (plan.RemoveIds.Count > 0 || plan.Above.Count > 0)
        {
            PinningFeed = true;
            try
            {
                FreezeViewport?.Invoke();
                if (plan.RemoveIds.Count > 0)
                {
                    var drop = new HashSet<int>(plan.RemoveIds);
                    for (var i = Messages.Count - 1; i >= 0; i--)
                    {
                        if (drop.Contains(Messages[i].Id))
                            Messages.RemoveAt(i);
                    }
                }

                foreach (var message in plan.Above)
                    InsertOrdered(message);
            }
            finally
            {
                PinningFeed = false;
            }

            RefreshShown();
            HoldViewport?.Invoke();
        }

        if (plan.Tail.Count > 0)
        {
            foreach (var message in plan.Tail)
                Messages.Add(ToItem(message));
            RefreshShown();
            return;
        }

        RefreshShown();
    }

    private void RefreshShown()
    {
        RestyleMessages();
        ApplyReceipts();
        FitPreviews();
    }

    private static Task PaintAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.CompletedTask;
        return dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task;
    }

    private void InsertOrdered(ChatMessage message)
    {
        var index = 0;
        while (index < Messages.Count && Messages[index].Id < message.Id)
            index++;
        if (index < Messages.Count && Messages[index].Id == message.Id)
            return;
        Messages.Insert(index, ToItem(message));
    }

    private int IndexOfMessage(int id)
    {
        for (var i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Id == id)
                return i;
        }

        return -1;
    }

    public int NewestShownId()
    {
        var peak = 0;
        foreach (var message in Messages)
        {
            if (message.Id > peak)
                peak = message.Id;
        }

        return peak;
    }

    private string _gapLog = "";

    private void NoteSelectedGap(string when)
    {
        if (SelectedDialog is not { } dialog)
            return;

        var feed = NewestShownId();
        if (dialog.LastMessageId <= feed)
            return;

        var line = dialog.DialogId + " " + dialog.LastMessageId + " " + feed + " " + when;
        if (line == _gapLog)
            return;

        _gapLog = line;
        FileLog.Write(FeedTrace.GapLine(dialog.DialogId, dialog.LastMessageId, feed, when));
    }

    private int OldestShownId()
    {
        var floor = int.MaxValue;
        foreach (var message in Messages)
        {
            if (message.Id < floor)
                floor = message.Id;
        }

        return floor == int.MaxValue ? 0 : floor;
    }

    private void SaveQuiet(string dialogId, IReadOnlyList<ChatMessage> messages)
    {
        var copy = messages.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                _database.SaveMessages(dialogId, copy);
            }
            catch (Exception exception)
            {
                FileLog.Write(exception);
            }
        });
    }

    private static void Forget(Task task)
    {
        _ = task.ContinueWith(
            completed => FileLog.Write(completed.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<ReactionPage?> FetchReactionsAsync(string dialogId)
    {
        try
        {
            return await _api.GetReactionsAsync(dialogId, FeedDiff.PageCount, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return null;
        }
    }

    private static void NoteChatId(DialogItem? dialog, ReactionPage? page, IReadOnlyList<ChatMessage> messages)
    {
        if (dialog is null)
            return;
        if (page is { ChatId: > 0 })
        {
            dialog.ChatId = page.ChatId;
            return;
        }

        if (dialog.ChatId > 0)
            return;
        foreach (var message in messages)
        {
            if (message.ChatId <= 0)
                continue;
            dialog.ChatId = message.ChatId;
            return;
        }
    }

    private static IReadOnlyList<ChatMessage> Stamp(IReadOnlyList<ChatMessage> messages, ReactionPage page)
    {
        if (page.MessageIds.Count == 0)
            return messages;

        var covered = new HashSet<int>(page.MessageIds);
        var list = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (!covered.Contains(message.Id))
            {
                list.Add(message);
                continue;
            }

            list.Add(message with { Reactions = page.For(message.Id) });
        }

        return list;
    }

    private bool ApplyReactionPage(string dialogId, ReactionPage page, bool visible)
    {
        var covered = new HashSet<int>(page.MessageIds);
        var changed = false;
        if (visible)
        {
            foreach (var item in Messages)
            {
                if (!covered.Contains(item.Id))
                    continue;
                if (item.SetReactions(page.For(item.Id), FaceDuringReload))
                    changed = true;
            }
        }

        _memory.ApplyPage(dialogId, page);
        return changed;
    }

    private ChatReactionFace MineFace()
    {
        var name = SelfName();
        return new ChatReactionFace(_sessions.Current.UserId, name, _selfUrl, AuthorColor(name));
    }

    private static bool IsHarmlessReaction(BitrixException exception)
    {
        var code = exception.ErrorCode ?? "";
        return code.Contains("ALREADY", StringComparison.OrdinalIgnoreCase)
            || code.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || code.Contains("NOT_EXISTS", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ToggleReactionAsync(int messageId, string code)
    {
        var dialog = SelectedDialog;
        if (dialog is null || messageId <= 0 || string.IsNullOrWhiteSpace(code))
            return;

        MessageItem? item = null;
        foreach (var message in Messages)
        {
            if (message.Id != messageId)
                continue;
            item = message;
            break;
        }

        if (item is null || item.IsSystem)
            return;

        var before = item.ReactionSource;
        var next = ReactionToggle.Apply(before, code, MineFace());
        item.SetReactions(next, FaceDuringReload);
        _memory.RememberReaction(dialog.DialogId, messageId, next);
        var removing = false;
        foreach (var reaction in before)
        {
            if (reaction.Code != code || !reaction.Mine)
                continue;
            removing = true;
            break;
        }

        try
        {
            if (removing)
                await _api.DeleteReactionAsync(messageId, code, CancellationToken.None);
            else
                await _api.AddReactionAsync(messageId, code, CancellationToken.None);
            _database.SaveReactions(dialog.DialogId, messageId, next);
        }
        catch (BitrixException exception) when (IsHarmlessReaction(exception))
        {
            _database.SaveReactions(dialog.DialogId, messageId, next);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            if (SelectedDialog?.DialogId != dialog.DialogId)
                return;
            foreach (var message in Messages)
            {
                if (message.Id != messageId)
                    continue;
                message.SetReactions(before, FaceDuringReload);
                break;
            }

            _memory.RememberReaction(dialog.DialogId, messageId, before);
            Error = exception.Message;
        }
    }

    private async Task AppendTailAsync(string dialogId, int version, bool revealMine = false)
    {
        var last = NewestShownId();
        var listed = SelectedDialog?.DialogId == dialogId
            ? SelectedDialog.LastMessageId
            : Dialogs.FirstOrDefault(item => item.DialogId == dialogId)?.LastMessageId ?? 0;
        var fresh = last == 0
            ? await _api.GetMessagesAsync(dialogId, null, null, CancellationToken.None)
            : await _api.GetMessagesAsync(dialogId, last, null, CancellationToken.None);
        var page = await FetchReactionsAsync(dialogId);
        if (page is not null)
        {
            NoteChatId(Dialogs.FirstOrDefault(item => item.DialogId == dialogId), page, fresh);
            fresh = Stamp(fresh, page);
        }

        _memory.RememberMore(dialogId, fresh);
        if (page is not null)
            ApplyReactionPage(dialogId, page, version == _loadVersion && SelectedDialog?.DialogId == dialogId);
        SaveQuiet(dialogId, page is not null ? _memory.Copy(dialogId) : fresh);
        CaptureOutgoing(dialogId, fresh);
        if (version != _loadVersion || SelectedDialog?.DialogId != dialogId)
            return;

        var userId = _sessions.Current.UserId;
        var added = 0;
        foreach (var message in fresh)
        {
            if (Messages.All(item => item.Id != message.Id))
            {
                var item = ToItem(message);
                if (revealMine && !message.IsSystem && message.AuthorId == userId && LinkText.FirstTarget(message.Text) is null)
                    item.MarkArrive();
                FitMessage(item);
                Messages.Add(item);
                added++;
            }
            if (SelectedDialog?.DialogId == dialogId)
                SelectedDialog.NoteLatest(userId, message);
        }

        RestyleMessages();
        ApplyReceipts();
        var feed = NewestShownId();
        FileLog.Write(FeedTrace.TailLine(dialogId, last, fresh.Count, feed, listed, added));
        if (listed > feed)
            FileLog.Write(FeedTrace.GapLine(dialogId, listed, feed, "после догрузки"));
        if (added > 0)
            FollowTail?.Invoke();
        if (SelectedDialog is not null)
            await MarkReadAsync(SelectedDialog, fresh);
        if (SelectedDialog?.DialogId == dialogId)
            RequestReceipt(SelectedDialog);
    }

    private async Task MarkReadAsync(DialogItem dialog, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return;

        var last = messages.Max(message => message.Id);
        if (last <= 0)
            return;

        try
        {
            await _api.MarkReadAsync(dialog.DialogId, last, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        _database.SetLastRead(dialog.DialogId, last);
        dialog.Unread = 0;
        PublishUnread();
    }

    public int UnreadTotal { get; private set; }
    public bool HasUnreadMessages => UnreadTotal > 0;
    public string UnreadMessageLabel => UnreadTotal > 99 ? "99+" : UnreadTotal.ToString();

    private void PublishUnread()
    {
        var total = 0;
        foreach (var dialog in Dialogs)
        {
            if (dialog.Unread > 0)
                total += dialog.Unread;
        }

        if (total == UnreadTotal)
            return;

        UnreadTotal = total;
        OnPropertyChanged(nameof(UnreadTotal));
        OnPropertyChanged(nameof(HasUnreadMessages));
        OnPropertyChanged(nameof(UnreadMessageLabel));
    }

    private void CaptureOutgoing(string dialogId, IReadOnlyList<ChatMessage> fresh)
    {
        if (_outgoingLink is not { } pending || pending.DialogId != dialogId)
            return;

        ChatMessage? found = null;
        foreach (var message in fresh)
        {
            if (message.IsSystem || message.AuthorId != _sessions.Current.UserId || message.Id <= pending.AfterId)
                continue;
            if (found is null || message.Id < found.Id)
                found = message;
        }

        if (found is null)
            return;

        _outgoingLink = null;
        var url = LinkText.FirstTarget(found.Text);
        if (url is null)
            return;
        _ = FetchLinkAsync(dialogId, found.Id, url);
    }

    private void NoteEditedLink(string dialogId, MessageItem message, string text)
    {
        var url = LinkText.FirstTarget(text);
        var current = StoredPreview(dialogId, message.Id);
        if (url is null)
        {
            if (current is null)
                return;
            _database.DeleteLinkPreview(dialogId, message.Id);
            if (_linkPreviews.TryGetValue(dialogId, out var map))
                map.Remove(message.Id);
            _linkFetch.Remove((dialogId, message.Id));
            message.ClearLink();
            return;
        }

        if (current is not null && string.Equals(current.Url, url, StringComparison.Ordinal))
            return;

        if (current is not null)
        {
            _database.DeleteLinkPreview(dialogId, message.Id);
            if (_linkPreviews.TryGetValue(dialogId, out var map))
                map.Remove(message.Id);
        }

        message.ClearLink();
        _ = FetchLinkAsync(dialogId, message.Id, url);
    }

    private async Task FetchLinkAsync(string dialogId, int messageId, string url)
    {
        var key = (dialogId, messageId);
        _linkFetch[key] = url;
        try
        {
            var folder = Path.Combine(AppPaths.Create().Directory, "link-previews");
            var card = await LinkPreviewClient.FetchAsync(url, folder, CancellationToken.None);
            if (card is null)
                return;
            if (!_linkFetch.TryGetValue(key, out var expected) || !string.Equals(expected, url, StringComparison.Ordinal))
                return;

            _database.SaveLinkPreview(dialogId, messageId, card);
            if (_linkPreviews.TryGetValue(dialogId, out var map))
                map[messageId] = card;
            if (SelectedDialog?.DialogId != dialogId)
                return;

            var index = IndexOfMessage(messageId);
            if (index < 0)
                return;
            Messages[index].ShowLink(card.Url, card.Title, card.Description, LinkImage.Decode(card.ImagePath));
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private LinkPreview? StoredPreview(string dialogId, int messageId)
    {
        if (!_linkPreviews.TryGetValue(dialogId, out var map))
        {
            try
            {
                map = _database.GetLinkPreviews(dialogId);
            }
            catch (Exception exception)
            {
                FileLog.Write(exception);
                map = new Dictionary<int, LinkPreview>();
            }

            _linkPreviews[dialogId] = map;
        }

        return map.TryGetValue(messageId, out var preview) ? preview : null;
    }

    private static string AuthorColor(string name)
    {
        var palette = new[] { "#00ACE3", "#2FC6F6", "#9DCF00", "#FFA900", "#F16E8A", "#A77BDE" };
        var hash = 0;
        foreach (var character in name)
            hash = unchecked(hash * 31 + character);
        var index = hash % palette.Length;
        if (index < 0)
            index += palette.Length;
        return palette[index];
    }

    private MessageItem ToItem(ChatMessage message, bool fade = true)
    {
        var userId = _sessions.Current.UserId;
        var item = new MessageItem
        {
            Id = message.Id,
            Author = message.AuthorName,
            Text = message.Text,
            TimeLabel = TextCleaner.MessageTime(message.SentAt),
            SentAt = message.SentAt,
            IsMine = !message.IsSystem && message.AuthorId == userId,
            IsSystem = message.IsSystem,
            ShowAuthor = !message.IsSystem && message.AuthorId != userId && SelectedDialog is { IsGroup: true },
            Initials = TextCleaner.Initials(message.AuthorName),
            Color = AuthorColor(message.AuthorName),
            Files = FileItems(message),
            IsEdited = message.IsEdited
        };
        if (fade && _fadeGeneration != 0)
            item.MarkFade();
        item.RememberAuthor(message.AuthorId, AvatarFor(message));
        ApplyMessageFace(item, _refreshFaces != 0);
        if (message.Reactions is not null)
            item.SetReactions(message.Reactions, FaceDuringReload);
        if (StoredPreview(message.DialogId, message.Id) is { } preview)
            item.ShowLink(preview.Url, preview.Title, preview.Description, LinkImage.Decode(preview.ImagePath));

        foreach (var file in item.Files)
            _previews.Queue(file, _sessions.Current.AccessToken);

        FitMessage(item);
        return item;
    }

    private async Task WarmSelfAsync(bool force)
    {
        try
        {
            var profile = await _api.GetCurrentUserAsync(CancellationToken.None).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return;
            await dispatcher.InvokeAsync(() =>
            {
                _selfId = profile.Id;
                _selfUrl = profile.AvatarUrl;
                _selfKnown = true;
                SyncAvatars(force);
            });
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return;
            await dispatcher.InvokeAsync(() => SyncAvatars(force));
        }
    }

    private void NoteSelf()
    {
        var id = _sessions.Current.UserId;
        if (id <= 0 || id == _selfId)
            return;
        _selfId = id;
        _selfUrl = null;
        _selfKnown = false;
        SelfFace = null;
        _ = WarmSelfAsync(false);
    }

    private void PaintSelf()
    {
        var id = _selfId > 0 ? _selfId : _sessions.Current.UserId;
        SelfFace = id > 0 ? _avatars.Circle(id, 42) : null;
    }

    private string SelfName() => string.IsNullOrWhiteSpace(UserName) ? "Я" : UserName;

    private void SyncAvatars(bool force)
    {
        NoteSelf();
        _prefetch.Clear();
        foreach (var id in AvatarCachePolicy.FirstEmployees(Dialogs.Select(dialog => (dialog.IsGroup, dialog.ProfileUserId))))
            _prefetch.Add(id);

        if (_selfId > 0)
            _avatars.Ask(_selfId, _selfUrl, _selfKnown, force);

        foreach (var dialog in Dialogs)
            PaintDialog(dialog, force);

        foreach (var id in _avatars.TrackedIds())
        {
            if (id == _selfId || Dialogs.Any(dialog => dialog.ProfileUserId == id))
                continue;
            _avatars.Ask(id, null, false, force);
        }

        PaintSelf();
    }

    private void TouchOpenDialog(DialogItem dialog)
    {
        if (dialog.IsGroup || dialog.ProfileUserId <= 0)
            return;
        _avatars.Ask(dialog.ProfileUserId, dialog.AvatarUrl, dialog.AvatarKnown, _refreshFaces != 0);
        PaintDialog(dialog, _refreshFaces != 0);
    }

    private void PaintDialog(DialogItem item, bool force)
    {
        if (item.IsGroup)
        {
            item.ShowRemote(item.AvatarUrl);
            return;
        }

        var id = item.ProfileUserId;
        if (id <= 0)
        {
            item.ShowRemote(null);
            return;
        }

        var prefetch = _prefetch.Contains(id);
        var opened = prefetch || _avatars.IsTracked(id) || SelectedDialog?.ProfileUserId == id;
        if (opened)
            _avatars.Ask(id, item.AvatarUrl, item.AvatarKnown, force);

        var list = _avatars.Circle(id, 48);
        var talk = _avatars.Circle(id, 42);
        if (list is not null || talk is not null)
            item.ShowCached(list, talk);
        else if (!prefetch && !string.IsNullOrWhiteSpace(item.AvatarUrl))
            item.ShowRemote(item.AvatarUrl);
        else
            item.ShowRemote(null);
    }

    private void OnAvatarReady(int userId)
    {
        foreach (var dialog in Dialogs)
        {
            if (dialog.ProfileUserId == userId)
                PaintDialog(dialog, false);
        }

        if (userId == _selfId || userId == _sessions.Current.UserId)
            PaintSelf();

        foreach (var message in Messages)
        {
            if (message.AuthorId == userId)
                ApplyMessageFace(message, false);
            if (Mentions(message, userId))
                message.RepaintFaces((face, height) => FaceOf(face, height, false));
        }
    }

    private void ApplyMessageFace(MessageItem item, bool force)
    {
        if (item.IsSystem || item.AuthorId <= 0)
        {
            item.AvatarImage = null;
            return;
        }

        if (item.AuthorId == _selfId || item.AuthorId == _sessions.Current.UserId)
        {
            var own = !string.IsNullOrWhiteSpace(_selfUrl) ? _selfUrl : item.AuthorAvatarUrl;
            if (_selfKnown && string.IsNullOrWhiteSpace(own))
                _avatars.Ask(item.AuthorId, null, true, force);
            else
                _avatars.Ask(item.AuthorId, own, !string.IsNullOrWhiteSpace(own), force);
            item.AvatarImage = _avatars.Circle(item.AuthorId, 42);
            return;
        }

        var url = item.AuthorAvatarUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            item.AvatarImage = null;
            return;
        }

        _avatars.Ask(item.AuthorId, url, true, force);
        item.AvatarImage = _avatars.Circle(item.AuthorId, 42);
    }

    private ImageSource? FaceDuringReload(ChatReactionFace face, double height) =>
        FaceOf(face, height, _refreshFaces != 0);

    private ImageSource? FaceOf(ChatReactionFace face, double height, bool force)
    {
        if (face.Id <= 0)
            return null;

        var size = (int)Math.Round(height);
        if (size <= 0)
            return null;

        var self = face.Id == _selfId || face.Id == _sessions.Current.UserId;
        var url = self && !string.IsNullOrWhiteSpace(_selfUrl) ? _selfUrl : face.AvatarUrl;
        if (self && _selfKnown && string.IsNullOrWhiteSpace(url))
            _avatars.Ask(face.Id, null, true, force);
        else if (!string.IsNullOrWhiteSpace(url) || self)
            _avatars.Ask(face.Id, url, !string.IsNullOrWhiteSpace(url), force);
        else
            return null;

        return _avatars.Square(face.Id, size);
    }

    private static bool Mentions(MessageItem message, int userId)
    {
        foreach (var reaction in message.ReactionSource)
        {
            foreach (var face in reaction.Faces)
            {
                if (face.Id == userId)
                    return true;
            }
        }

        return false;
    }

    private string? AvatarFor(ChatMessage message)
    {
        if (message.IsSystem)
            return null;
        if (message.AuthorId == _sessions.Current.UserId)
        {
            if (!string.IsNullOrWhiteSpace(_selfUrl))
                return _selfUrl;
            return string.IsNullOrWhiteSpace(message.AuthorAvatarUrl) ? null : message.AuthorAvatarUrl;
        }
        var avatar = message.AuthorAvatarUrl;
        if (string.IsNullOrWhiteSpace(avatar) && SelectedDialog is not { IsGroup: true })
            avatar = SelectedDialog?.AvatarUrl;
        return string.IsNullOrWhiteSpace(avatar) ? null : avatar;
    }

    private static FileItem[] FileItems(ChatMessage message)
    {
        var files = new FileItem[message.Files.Count];
        for (var i = 0; i < files.Length; i++)
        {
            var file = message.Files[i];
            files[i] = new FileItem(file.Name, file.Url, file.Size, file.Preview, file.Id);
        }

        return files;
    }

    private void RestyleMessages()
    {
        string? previousDay = null;
        for (var index = 0; index < Messages.Count; index++)
        {
            var item = Messages[index];
            var day = item.IsSystem ? "" : DayTitle(item.SentAt);
            item.DayLabel = day.Length > 0 && day != previousDay ? day : "";
            if (day.Length > 0)
                previousDay = day;

            var next = index + 1 < Messages.Count ? Messages[index + 1] : null;
            item.ShowAvatar = !item.IsSystem
                && (next is null
                    || next.IsSystem
                    || next.IsMine != item.IsMine
                    || next.Author != item.Author
                    || next.SentAt?.ToLocalTime().Date != item.SentAt?.ToLocalTime().Date);
        }
    }

    private void ApplyReceipts()
    {
        var upTo = SelectedDialog?.OthersReadMessageId ?? 0;
        foreach (var item in Messages)
            item.IsViewed = item.ShowReceipt && item.Id <= upTo;
    }

    private void RequestReceipt(DialogItem dialog)
    {
        if (_sessions.Current.UserId <= 0)
            return;
        var version = _loadVersion;
        var ticket = ++_receiptTicket;
        var dialogId = dialog.DialogId;
        _ = FetchReceiptAsync(dialogId, version, ticket);
    }

    private async Task FetchReceiptAsync(string dialogId, int version, int ticket)
    {
        try
        {
            var read = await _api.GetOthersReadMessageIdAsync(dialogId, CancellationToken.None);
            if (read is not int cursor || ticket != _receiptTicket || version != _loadVersion)
                return;
            var dialog = SelectedDialog;
            if (dialog is null || dialog.DialogId != dialogId)
                return;
            if (dialog.OthersReadMessageId != cursor)
                _database.SetOthersRead(dialogId, cursor);
            dialog.NoteCursor(cursor);
            ApplyReceipts();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private static string DayTitle(DateTimeOffset? sent)
    {
        if (sent is null)
            return "";

        var local = sent.Value.ToLocalTime();
        var day = local.Date;
        if (day == DateTime.Today)
            return "сегодня";
        if (day == DateTime.Today.AddDays(-1))
            return "вчера";

        var text = local.ToString("dddd, d MMMM", Russian);
        if (text.Length == 0)
            return "";
        return char.ToUpper(text[0], Russian) + text[1..];
    }

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
}
