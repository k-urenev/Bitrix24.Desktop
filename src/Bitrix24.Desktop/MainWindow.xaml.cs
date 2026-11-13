using System.IO;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Bitrix24.Desktop.Controls;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.ViewModels;

namespace Bitrix24.Desktop;

public partial class MainWindow : Window
{
    private bool _stickyQueued;
    private bool _olderArmed = true;
    private bool _holdingOlder;
    private double _anchorExtent;
    private double _anchorOffset;
    private bool? _chatShadowShown;
    private ScrollViewer? _messageScroll;
    private bool _userMovedFeed;
    private int _openGeneration = -1;
    private int _openUnread;
    private int _jumpFade;
    private bool _jumpShown;
    private double _feedExtent;
    private double _feedOffset;
    private int _feedEpoch;
    private bool _feedRevealArmed;
    private long _feedRevealAt;

    private const double FeedFadeSeconds = 0.2;
    private const double FeedStepSeconds = 0.05;
    private const double FeedHoldSeconds = 0.1;

    private const double ReactZoneSide = 80;

    private MessageItem? _reactMessage;
    private FrameworkElement? _reactRow;
    private FrameworkElement? _reactButton;
    private DispatcherTimer? _openTimer;
    private DispatcherTimer? _closeTimer;
    private bool _closeSmiley;
    private FileItem? _viewerFile;
    private int _viewerTicket;
    private bool _hintAlignQueued;
    private int _hintRetries;
    private double _hintX = double.NaN;
    private double _hintY = double.NaN;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewerFrame.Measure();
        InitializeComponent();
        DataContext = viewModel;
        ReactionChoices.ItemsSource = ReactionCatalog.Order
            .Select(code => new ReactionChoice(code, ReactionCatalog.Title(code), ReactionImages.Shared.For(code)))
            .ToList();
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FreezeViewport += FreezeFeed;
        viewModel.HoldViewport += HoldFeed;
        viewModel.FollowTail += OnNewMessage;
        viewModel.PlaceOpen += OnPlaceOpen;
        viewModel.EditArmed += OnEditArmed;
        SourceInitialized += (_, _) => WindowFit.Apply(this);
        Composer.ClipboardImageName = () => ViewModel.NextClipboardImageName();
        Composer.ImagesPasted += OnClipboardImages;
        Composer.SizeChanged += (_, _) => QueueComposerHint();
        ComposerHost.SizeChanged += (_, _) => QueueComposerHint();
        ComposerHint.IsVisibleChanged += (_, _) => QueueComposerHint();
        Loaded += (_, _) =>
        {
            WindowFit.Apply(this);
            MessagesList.ApplyTemplate();
            _messageScroll = FindVisual<ScrollViewer>(MessagesList);
            if (_messageScroll is not null)
            {
                _messageScroll.ScrollChanged += OnMessageScrollChanged;
                _messageScroll.PreviewMouseDown += (_, _) => _userMovedFeed = true;
                _messageScroll.PreviewMouseWheel += (_, _) => _userMovedFeed = true;
                _messageScroll.PreviewKeyDown += OnFeedKeyDown;
            }

            MessagesList.LayoutUpdated += (_, _) => RequestStickyDay();
            QueueComposerHint();
            SmoothScroll.Attach(DialogList);
            DialogList.ApplyTemplate();
            if (FindVisual<ScrollViewer>(DialogList) is ScrollViewer dialogs)
            {
                dialogs.ScrollChanged += (_, _) => UpdateChatShadow(dialogs.VerticalOffset);
                UpdateChatShadow(dialogs.VerticalOffset);
            }

            SizeChanged += (_, _) => NoteChatLayout();
            NoteChatLayout();
        };
    }

    public MainViewModel ViewModel { get; }

    public event EventHandler? SignOutRequested;

    public void RevealDialog(string dialogId)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var item = ViewModel.Dialogs.FirstOrDefault(dialog => dialog.DialogId == dialogId);
            if (item is not null && !ReferenceEquals(ViewModel.SelectedDialog, item))
                ViewModel.SelectDialog(dialogId);
            if (item is not null)
                DialogList.ScrollIntoView(item);
        });
    }

    private void OnOpenPeerProfile(object sender, MouseButtonEventArgs e)
    {
        var dialog = ViewModel.SelectedDialog;
        if (dialog is null || dialog.ProfileUserId <= 0)
            return;

        var url = PortalAddress.Absolute(
            ViewModel.PortalHost,
            "/company/personal/user/" + dialog.ProfileUserId + "/");
        if (url is null || !PortalAddress.IsPortalHttps(ViewModel.PortalHost, url))
            return;

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnOpenTaskNotice(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskNoticeItem notice })
            return;
        if (string.IsNullOrWhiteSpace(notice.Url))
            return;
        Process.Start(new ProcessStartInfo(notice.Url) { UseShellExecute = true });
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e) =>
        SignOutRequested?.Invoke(this, EventArgs.Empty);

    private void OnNoticesPageVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement page)
            ViewModel.SetNoticesOpen(page.IsVisible);
    }

    private void OnChatsPageVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement { IsVisible: true })
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, NoteChatLayout);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _holdingOlder = false;
            _olderArmed = true;
            _feedEpoch++;
            _feedRevealArmed = false;
            CloseReactions();
            ClosePicture();
        }

        RequestStickyDay();
        NoteChatLayout();
    }

    private void OnNewMessage() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollFeedToEnd);

    private void OnMessageRowReady(object sender, RoutedEventArgs e) =>
        ArmFeedRow(sender as FrameworkElement);

    private void OnMessageRowContext(object sender, DependencyPropertyChangedEventArgs e) =>
        ArmFeedRow(sender as FrameworkElement);

    private void ArmFeedRow(FrameworkElement? row)
    {
        if (row is null)
            return;

        row.BeginAnimation(UIElement.OpacityProperty, null);
        row.ClearValue(UIElement.OpacityProperty);
        if (row.DataContext is not MessageItem item)
            return;

        var epoch = _feedEpoch;
        if (item.FadeOnArrive)
        {
            ShowWhenReady(row, item, epoch, () => FadeArriveRow(row, item, epoch));
            return;
        }

        if (!item.FadeOnOpen)
            return;

        ShowWhenReady(row, item, epoch, () => FadeFeedRow(row, item, epoch));
    }

    private void ShowWhenReady(FrameworkElement row, MessageItem item, int epoch, Action start, int tries = 0)
    {
        if (epoch != _feedEpoch || !ReferenceEquals(row.DataContext, item))
            return;

        var bubble = FindBubble(row);
        if (bubble is { IsChecked: true } || tries >= 45)
        {
            if (bubble is not { IsChecked: true })
                FileLog.Write($"пузырь {item.Id} сверка не дождалась ширины");
            start();
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () => ShowWhenReady(row, item, epoch, start, tries + 1));
    }

    private static BubbleLayout? FindBubble(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is BubbleLayout bubble)
                return bubble;
            var nested = FindBubble(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private void FadeArriveRow(FrameworkElement row, MessageItem item, int epoch)
    {
        if (epoch != _feedEpoch || !ReferenceEquals(row.DataContext, item) || !item.FadeOnArrive)
            return;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromSeconds(FeedHoldSeconds),
            Duration = TimeSpan.FromSeconds(FeedFadeSeconds)
        };
        animation.Completed += (_, _) =>
        {
            if (epoch != _feedEpoch || !ReferenceEquals(row.DataContext, item))
                return;
            item.FinishArrive();
            row.BeginAnimation(UIElement.OpacityProperty, null);
            row.ClearValue(UIElement.OpacityProperty);
        };
        row.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void FadeFeedRow(FrameworkElement row, MessageItem item, int epoch)
    {
        if (epoch != _feedEpoch || !ReferenceEquals(row.DataContext, item) || !item.FadeOnOpen)
            return;

        var steps = StepsFromNewest(item);
        if (steps < 0)
            return;

        if (!_feedRevealArmed)
        {
            _feedRevealArmed = true;
            _feedRevealAt = Stopwatch.GetTimestamp();
        }

        var elapsed = Stopwatch.GetElapsedTime(_feedRevealAt);
        var due = TimeSpan.FromSeconds(FeedHoldSeconds + FeedStepSeconds * steps);
        var fade = TimeSpan.FromSeconds(FeedFadeSeconds);
        if (elapsed >= due + fade)
        {
            FinishFeedRow(row, item);
            return;
        }

        var started = elapsed > due ? elapsed - due : TimeSpan.Zero;
        var animation = new DoubleAnimation
        {
            From = Math.Clamp(started.TotalSeconds / FeedFadeSeconds, 0, 1),
            To = 1,
            BeginTime = elapsed < due ? due - elapsed : TimeSpan.Zero,
            Duration = fade - started
        };
        animation.Completed += (_, _) =>
        {
            if (epoch != _feedEpoch || !ReferenceEquals(row.DataContext, item))
                return;
            FinishFeedRow(row, item);
        };
        row.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void FinishFeedRow(FrameworkElement row, MessageItem item)
    {
        item.FinishFade();
        row.BeginAnimation(UIElement.OpacityProperty, null);
        row.ClearValue(UIElement.OpacityProperty);
    }

    private int StepsFromNewest(MessageItem item)
    {
        var messages = ViewModel.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (!ReferenceEquals(messages[i], item))
                continue;
            return messages.Count - 1 - i;
        }

        return -1;
    }

    private void OnPlaceOpen(int generation, int unread)
    {
        if (generation != ViewModel.OpenGeneration)
            return;

        if (generation != _openGeneration)
        {
            _openGeneration = generation;
            _userMovedFeed = false;
        }

        if (_userMovedFeed)
            return;

        _openUnread = unread;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceOpenedChat);
    }

    private void OnFeedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
            _userMovedFeed = true;
    }

    private void ScrollFeedToEnd()
    {
        if (_messageScroll is null || MessagesList.Items.Count == 0)
            return;

        var panel = FindVisual<VirtualizingPanel>(MessagesList);
        panel?.BringIndexIntoViewPublic(MessagesList.Items.Count - 1);
        _messageScroll.UpdateLayout();
        _messageScroll.ScrollToEnd();
    }

    private void PlaceOpenedChat()
    {
        if (_userMovedFeed || _messageScroll is null || MessagesList.Items.Count == 0)
            return;

        var unread = UnreadMessages();
        if (unread.Count == 0)
        {
            ScrollFeedToEnd();
            return;
        }

        var panel = FindVisual<VirtualizingPanel>(MessagesList);
        panel?.BringIndexIntoViewPublic(MessagesList.Items.Count - 1);
        _messageScroll.UpdateLayout();
        _messageScroll.ScrollToEnd();
        _messageScroll.UpdateLayout();

        double sum = 0;
        var measured = true;
        foreach (var message in unread)
        {
            if (MessagesList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement row
                || row.ActualHeight <= 0)
            {
                measured = false;
                break;
            }

            sum += row.ActualHeight;
        }

        if (measured && sum < _messageScroll.ViewportHeight)
            return;

        var oldest = unread[0];
        var index = MessagesList.Items.IndexOf(oldest);
        if (index >= 0)
            panel?.BringIndexIntoViewPublic(index);
        MessagesList.ScrollIntoView(oldest);
        _messageScroll.UpdateLayout();
        if (MessagesList.ItemContainerGenerator.ContainerFromItem(oldest) is not FrameworkElement anchor)
            return;
        if (!TryViewportY(anchor, out var y))
            return;

        _messageScroll.ScrollToVerticalOffset(Math.Max(0, _messageScroll.VerticalOffset + y));
    }

    private List<MessageItem> UnreadMessages()
    {
        var rows = new UnreadAnchor.Row[ViewModel.Messages.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new UnreadAnchor.Row(ViewModel.Messages[i].Id, ViewModel.Messages[i].IsMine);
        var ids = UnreadAnchor.Collect(rows, _openUnread);
        var messages = new List<MessageItem>(ids.Count);
        foreach (var id in ids)
        {
            foreach (var message in ViewModel.Messages)
            {
                if (message.Id != id)
                    continue;
                messages.Add(message);
                break;
            }
        }

        return messages;
    }

    private void FreezeFeed()
    {
        if (_messageScroll is null)
            return;

        _feedExtent = _messageScroll.ExtentHeight;
        _feedOffset = _messageScroll.VerticalOffset;
    }

    private void HoldFeed()
    {
        if (_messageScroll is null)
            return;

        _messageScroll.UpdateLayout();
        var grown = _messageScroll.ExtentHeight - _feedExtent;
        _messageScroll.ScrollToVerticalOffset(Math.Max(0, _feedOffset + grown));
    }

    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_messageScroll is null)
            return;

        if (e.ViewportWidthChange != 0)
            NoteChatLayout();
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0)
            CloseReactions();

        UpdateStickyDay();
        TryLoadOlder();
    }

    private void NoteChatLayout()
    {
        if (_messageScroll is null)
            return;

        ViewModel.NoteChatLayout(ActualWidth, ActualHeight, _messageScroll.ViewportWidth);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy))
            RestoreOlderAnchor();
    }

    private void TryLoadOlder()
    {
        if (_messageScroll is null || _holdingOlder || _messageScroll.ScrollableHeight <= 0)
            return;

        if (!OlderButtonVisible())
        {
            _olderArmed = true;
            return;
        }

        if (!_olderArmed || !ViewModel.LoadOlderCommand.CanExecute(null))
            return;

        _olderArmed = false;
        _holdingOlder = true;
        _anchorExtent = _messageScroll.ExtentHeight;
        _anchorOffset = _messageScroll.VerticalOffset;
        ViewModel.LoadOlderCommand.Execute(null);
    }

    private void RestoreOlderAnchor()
    {
        if (!_holdingOlder || ViewModel.IsBusy)
            return;

        if (_messageScroll is null)
            return;

        _messageScroll.UpdateLayout();
        var grown = _messageScroll.ExtentHeight - _anchorExtent;
        if (grown > 0)
            _messageScroll.ScrollToVerticalOffset(_anchorOffset + grown);
        _holdingOlder = false;
    }

    private void UpdateChatShadow(double offset)
    {
        var show = offset > 1;
        if (_chatShadowShown == show)
            return;

        _chatShadowShown = show;
        ChatListShadow.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromSeconds(0.2)
        });
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && (match is null || match(found)))
                return found;

            var nested = FindVisual(child, match);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static bool IsOlderButton(Button button) =>
        button.Content is TextBlock { Text: "Загрузить раньше" };

    private bool OlderButtonVisible()
    {
        if (_messageScroll is null)
            return false;
        if (MessagesList.ItemContainerGenerator.ContainerFromIndex(0) is not DependencyObject row)
            return false;
        if (FindVisual<Button>(row, IsOlderButton) is not Button button)
            return false;
        if (button.Visibility != Visibility.Visible || button.ActualHeight <= 0)
            return false;
        if (!TryViewportY(button, out var y))
            return false;

        return y < _messageScroll.ViewportHeight && y + button.ActualHeight > 0;
    }

    private void RequestStickyDay()
    {
        if (_stickyQueued)
            return;

        _stickyQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _stickyQueued = false;
            UpdateStickyDay();
        });
    }

    private void UpdateStickyDay()
    {
        if (_messageScroll is null)
        {
            StickyDay.Visibility = Visibility.Collapsed;
            ShowJumpLatest(false);
            return;
        }

        UpdateJumpLatest();

        var count = MessagesList.Items.Count;
        var first = -1;
        var firstTop = 0d;
        for (var i = 0; i < count; i++)
        {
            if (MessagesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            if (!TryViewportY(row, out var y) || y + row.ActualHeight <= 0)
                continue;

            first = i;
            firstTop = y;
            break;
        }

        var label = "";
        if (first >= 0)
        {
            var owner = firstTop > 0 ? first - 1 : first;
            for (var i = owner; i >= 0; i--)
            {
                if (MessagesList.Items[i] is MessageItem message && message.HasDay)
                {
                    label = message.DayLabel;
                    break;
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (MessagesList.Items[i] is not MessageItem message || !message.HasDay)
                continue;
            if (MessagesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            if (DayChip(row, message.DayLabel) is not FrameworkElement chip || !TryViewportY(chip, out var y))
                continue;

            chip.Opacity = label.Length > 0 && message.DayLabel == label && y <= 0 ? 0 : 1;
        }

        if (label.Length == 0)
        {
            StickyDay.Visibility = Visibility.Collapsed;
            return;
        }

        StickyDayText.Text = label;
        StickyDay.Visibility = Visibility.Visible;
    }

    private void UpdateJumpLatest()
    {
        if (_messageScroll is null)
        {
            ShowJumpLatest(false);
            return;
        }

        var viewport = _messageScroll.ViewportHeight;
        var count = MessagesList.Items.Count;
        var visible = 0;
        var newestSeen = -1;
        for (var i = 0; i < count; i++)
        {
            if (MessagesList.Items[i] is not MessageItem)
                continue;
            if (MessagesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            if (!TryViewportY(row, out var y) || y >= viewport || y + row.ActualHeight <= 0)
                continue;

            visible++;
            newestSeen = i;
        }

        var past = 0;
        if (newestSeen >= 0)
        {
            for (var i = newestSeen + 1; i < count; i++)
            {
                if (MessagesList.Items[i] is MessageItem)
                    past++;
            }
        }

        ShowJumpLatest(visible > 0 && past * 2 > visible);
    }

    private void ShowJumpLatest(bool show)
    {
        if (show == _jumpShown)
            return;

        _jumpShown = show;
        var fade = ++_jumpFade;
        var from = JumpLatest.Visibility == Visibility.Visible ? JumpLatest.Opacity : 0;
        JumpLatest.BeginAnimation(OpacityProperty, null);
        JumpLatest.Opacity = from;
        JumpLatest.IsHitTestVisible = show;

        if (show)
            JumpLatest.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            From = from,
            To = show ? 1 : 0,
            Duration = TimeSpan.FromSeconds(0.3)
        };
        if (!show)
        {
            animation.Completed += (_, _) =>
            {
                if (fade != _jumpFade || _jumpShown)
                    return;

                JumpLatest.BeginAnimation(OpacityProperty, null);
                JumpLatest.Opacity = 0;
                JumpLatest.Visibility = Visibility.Collapsed;
            };
        }

        JumpLatest.BeginAnimation(OpacityProperty, animation);
    }

    private void OnJumpLatestClick(object sender, RoutedEventArgs e)
    {
        if (_messageScroll is null || MessagesList.Items.Count == 0)
            return;

        _userMovedFeed = true;
        _holdingOlder = false;
        ScrollFeedToEnd();
    }

    private bool TryViewportY(FrameworkElement element, out double y)
    {
        y = 0;
        if (_messageScroll is null)
            return false;

        try
        {
            y = element.TransformToAncestor(_messageScroll).Transform(new System.Windows.Point(0, 0)).Y;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static FrameworkElement? DayChip(DependencyObject container, string label)
    {
        var count = VisualTreeHelper.GetChildrenCount(container);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(container, i);
            if (child is Border { Child: TextBlock text } && text.Text == label)
                return (FrameworkElement)child;

            var nested = DayChip(child, label);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void OnDialogClick(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.HasSearch || e.OriginalSource is not DependencyObject source)
            return;
        if (ItemsControl.ContainerFromElement(DialogList, source) is not ListBoxItem { DataContext: DialogItem item })
            return;

        ViewModel.ClearSearch();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DialogList.ScrollIntoView(item));
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSearch();
        SearchBox.Focus();
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e) =>
        EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    private void OnEmojiChosen(object sender, EmojiChosenEventArgs e) =>
        Composer.InsertEmoji(e.Character);

    private void OnPickNoticeSound(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoticeSoundChoice choice })
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = choice.Title,
            Filter = "Звуковые файлы|*.mp3;*.wav;*.wma|Все файлы|*.*",
            CheckFileExists = true
        };
        var folder = Path.GetDirectoryName(choice.FullPath);
        if (!string.IsNullOrEmpty(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            ViewModel.UseNoticeSound(choice.Kind, dialog.FileName);
    }

    private void OnClearNoticeSound(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoticeSoundChoice choice })
            ViewModel.UseNoticeSound(choice.Kind, "");
    }

    private void OnSupportArchive(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Архив для поддержки",
            Filter = "Архив ZIP|*.zip",
            FileName = SupportArchive.FileName(DateTimeOffset.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var window = FeedTrace.Window(IsVisible, WindowState == WindowState.Minimized);
        ViewModel.PackSupport(dialog.FileName, window);
        if (!string.IsNullOrEmpty(ViewModel.SupportArchiveNote) || !File.Exists(dialog.FileName))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dialog.FileName + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private void OnClipboardImages(object? sender, ClipboardPaste paste)
    {
        if (paste.Failed)
        {
            ViewModel.Error = "Не удалось вставить изображение.";
            return;
        }

        foreach (var image in paste.Images)
            ViewModel.AddClipboardImage(image.Path, image.Temporary);
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Приложить файлы",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            ViewModel.AddPendingFiles(dialog.FileNames);
    }

    private void OnChatFileDrag(object sender, DragEventArgs e)
    {
        if (!ViewModel.HasSelection || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = DragDropEffects.Copy;
        FileDropHint.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnChatFileDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement area)
            return;

        var pos = e.GetPosition(area);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= area.ActualWidth || pos.Y >= area.ActualHeight)
            FileDropHint.Visibility = Visibility.Collapsed;
    }

    private void OnChatFileDrop(object sender, DragEventArgs e)
    {
        FileDropHint.Visibility = Visibility.Collapsed;
        if (!ViewModel.HasSelection || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        ViewModel.AddPendingFiles(files);
        e.Handled = true;
    }

    private void OnEditArmed()
    {
        EmojiPopup.IsOpen = false;
        CloseReactions();
        PlaceComposerCaretAtEnd();
    }

    private void PlaceComposerCaretAtEnd()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Composer.Focus();
            Composer.PlaceCaretAtEnd();
        });
    }

    private void QueueComposerHint()
    {
        if (_hintAlignQueued)
            return;

        _hintAlignQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AlignComposerHint);
    }

    private void AlignComposerHint()
    {
        _hintAlignQueued = false;
        if (!ComposerHint.IsVisible || ComposerHost.ActualWidth <= 0)
            return;

        var caret = Composer.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        if (caret.IsEmpty || caret.Height <= 0 || double.IsNaN(caret.X) || double.IsNaN(caret.Y))
        {
            if (_hintRetries < 4)
            {
                _hintRetries++;
                QueueComposerHint();
            }

            return;
        }

        _hintRetries = 0;

        var origin = Composer.TranslatePoint(new System.Windows.Point(caret.X, caret.Y), ComposerHost);
        if (double.IsNaN(origin.X) || double.IsNaN(origin.Y))
            return;

        var x = origin.X + 2;
        var room = ComposerHost.ActualWidth - x - 8;
        if (room > 0 && Math.Abs(ComposerHint.MaxWidth - room) >= 0.5)
            ComposerHint.MaxWidth = room;

        if (Math.Abs(_hintX - x) < 0.5 && Math.Abs(_hintY - origin.Y) < 0.5)
            return;

        _hintX = x;
        _hintY = origin.Y;
        ComposerHintShift.X = x;
        ComposerHintShift.Y = origin.Y;
    }

    private void OnEditMessage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageItem message })
            ViewModel.BeginEditCommand.Execute(message);
        e.Handled = true;
    }

    private void OnQuoteRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not MessageBody { DataContext: MessageItem message } body)
            return;
        if (!ViewModel.InsertQuote(message, body.PendingQuote))
            return;
        PlaceComposerCaretAtEnd();
        e.Handled = true;
    }

    private void OnMessageQuote(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return;
        if (sender is not FrameworkElement { DataContext: MessageItem message })
            return;
        if (!ViewModel.InsertQuote(message, message.Text))
            return;
        PlaceComposerCaretAtEnd();
        e.Handled = true;
    }

    private void OnMessageMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        var onBody = e.OriginalSource is DependencyObject source && FindAncestor<MessageBody>(source) is not null;
        if (onBody)
        {
            element.ContextMenu = null;
            return;
        }

        if (element.DataContext is not MessageItem message || !message.CanEdit)
        {
            element.ContextMenu = null;
            e.Handled = true;
            return;
        }

        var item = new MenuItem { Header = "Редактировать" };
        item.Click += (_, _) => ViewModel.BeginEditCommand.Execute(message);
        element.ContextMenu = new ContextMenu { Items = { item } };
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found)
                return found;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FileItem file })
            return;
        OpenPortalFile(file);
    }

    private void OnOpenLinkPreview(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageItem message })
            return;
        var url = message.LinkUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        e.Handled = true;
    }

    private void OnOpenPicture(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileItem file } || !file.IsImage || !file.HasFrame)
            return;
        e.Handled = true;
        OpenPicture(file);
    }

    private void OpenPicture(FileItem file)
    {
        EmojiPopup.IsOpen = false;
        CloseReactions();
        _viewerFile = file;
        PictureDownload.IsEnabled = file.HasLink && PortalAddress.IsPortalHttps(ViewModel.PortalHost, file.Url);
        PictureView.Source = file.Picture;
        PictureLayer.Visibility = Visibility.Visible;
        LoadViewerPicture(file);
    }

    private async void LoadViewerPicture(FileItem file)
    {
        var ticket = ++_viewerTicket;
        System.Windows.Media.ImageSource? source;
        try
        {
            source = await ViewModel.PictureForViewerAsync(file);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return;
        }

        if (ticket != _viewerTicket || !ReferenceEquals(_viewerFile, file) || source is null)
            return;
        PictureView.Source = source;
    }

    private void ClosePicture()
    {
        if (PictureLayer.Visibility != Visibility.Visible)
            return;
        _viewerTicket++;
        _viewerFile = null;
        PictureView.Source = null;
        PictureLayer.Visibility = Visibility.Collapsed;
    }

    private void OnPictureClose(object sender, RoutedEventArgs e)
    {
        ClosePicture();
        e.Handled = true;
    }

    private void OnPictureDownload(object sender, RoutedEventArgs e)
    {
        if (_viewerFile is not null)
            OpenPortalFile(_viewerFile);
        e.Handled = true;
    }

    private void OnPictureLayerClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (source is System.Windows.Controls.Image || FindAncestor<Button>(source) is not null)
            return;
        ClosePicture();
    }

    private void OnPictureWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_viewerFile is null || e.Delta == 0)
            return;

        var files = ViewModel.Messages
            .SelectMany(message => message.Files)
            .Where(file => file.IsImage && file.HasFrame)
            .ToList();
        var index = files.IndexOf(_viewerFile);
        if (index < 0)
            return;
        var next = index + (e.Delta < 0 ? 1 : -1);
        if (next < 0 || next >= files.Count)
            return;
        OpenPicture(files[next]);
    }

    private void OpenPortalFile(FileItem file)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
            return;
        if (!PortalAddress.IsPortalHttps(ViewModel.PortalHost, file.Url))
            return;

        Process.Start(new ProcessStartInfo(file.Url) { UseShellExecute = true });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PictureLayer.Visibility == Visibility.Visible)
        {
            ClosePicture();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && (EmojiPopup.IsOpen || ReactionPopup.IsOpen))
        {
            EmojiPopup.IsOpen = false;
            CloseReactions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsEditing && ChatsNav.IsChecked == true)
        {
            ViewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control && ChatsNav.IsChecked == true)
        {
            ViewModel.ReloadMessages();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (EmojiPopup.IsOpen || ReactionPopup.IsOpen))
        {
            EmojiPopup.IsOpen = false;
            CloseReactions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsEditing)
        {
            ViewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up
            && Keyboard.Modifiers == ModifierKeys.None
            && !EmojiPopup.IsOpen
            && !ReactionPopup.IsOpen
            && !HasLetter(ViewModel.Composer))
        {
            ViewModel.EditLatest();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        var send = ViewModel.SendOnEnter
            ? Keyboard.Modifiers == ModifierKeys.None
            : Keyboard.Modifiers == ModifierKeys.Control;
        if (send)
        {
            if (ViewModel.SendCommand.CanExecute(null))
                ViewModel.SendCommand.Execute(null);
            EmojiPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (sender is EmojiBox box)
            box.BreakLine();
        e.Handled = true;
    }

    private static bool HasLetter(string text)
    {
        foreach (var character in text)
        {
            if (char.IsLetter(character))
                return true;
        }

        return false;
    }

    private void OnMessageEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not MessageItem message || message.IsSystem)
            return;

        _closeTimer?.Stop();
        _closeSmiley = false;
        if (_reactMessage is not null && !ReferenceEquals(_reactMessage, message))
        {
            _reactMessage.ShowReact = false;
            ReactionPopup.IsOpen = false;
        }

        _reactRow = row;
        _reactMessage = message;
        message.ShowReact = true;
        row.UpdateLayout();
        TrackReactZone();
    }

    private void OnMessageMove(object sender, MouseEventArgs e) => TrackReactZone();

    private void OnMessageLeave(object sender, MouseEventArgs e) => ScheduleClose(hideSmiley: true);

    private void OnReactEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        _reactButton = element;
        if (element.DataContext is MessageItem message)
            _reactMessage = message;
        TrackReactZone();
    }

    private void TrackReactZone()
    {
        if (_reactMessage is null || _reactMessage.IsSystem)
            return;

        var anchor = ReactAnchor();
        if (anchor is null || anchor.ActualWidth <= 0)
            return;

        if (!InReactZone(anchor, Mouse.GetPosition(anchor)))
        {
            _openTimer?.Stop();
            if (ReactionPopup.IsOpen)
                ScheduleClose(hideSmiley: false);
            return;
        }

        _closeSmiley = false;
        _closeTimer?.Stop();
        if (!ReactionPopup.IsOpen)
            ArmPicker();
    }

    private static bool InReactZone(FrameworkElement anchor, System.Windows.Point point)
    {
        var left = (anchor.ActualWidth - ReactZoneSide) / 2;
        var top = (anchor.ActualHeight - ReactZoneSide) / 2;
        return point.X >= left && point.X <= left + ReactZoneSide
            && point.Y >= top && point.Y <= top + ReactZoneSide;
    }

    private void ArmPicker()
    {
        if (ReactionPopup.IsOpen || _openTimer is { IsEnabled: true })
            return;

        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            OpenPicker();
        };
        _openTimer.Start();
    }

    private void OnReactClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: MessageItem message })
            _reactMessage = message;
        if (_reactMessage is null)
            return;
        _ = ViewModel.ToggleReactionAsync(_reactMessage.Id, "like");
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ReactionChip chip })
            return;
        _ = ViewModel.ToggleReactionAsync(chip.MessageId, chip.Code);
    }

    private void OnPickReaction(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_reactMessage is null || sender is not FrameworkElement { DataContext: ReactionChoice choice })
            return;
        var messageId = _reactMessage.Id;
        CloseReactions();
        _ = ViewModel.ToggleReactionAsync(messageId, choice.Code);
    }

    private void OnReactionPopupEnter(object sender, MouseEventArgs e)
    {
        _closeSmiley = false;
        _closeTimer?.Stop();
    }

    private void OnReactionPopupLeave(object sender, MouseEventArgs e) =>
        ScheduleClose(hideSmiley: _reactRow?.IsMouseOver != true);

    private void OpenPicker()
    {
        if (_reactMessage is null || _reactMessage.IsSystem)
            return;

        _reactRow?.UpdateLayout();
        var anchor = ReactAnchor();
        if (anchor is null)
            return;

        _reactButton = anchor;
        ReactionPopup.PlacementTarget = anchor;
        ReactionPopup.IsOpen = true;
    }

    private FrameworkElement? ReactAnchor()
    {
        if (_reactRow is null || _reactMessage is null)
            return _reactButton;

        var name = _reactMessage.IsMine ? "ReactBefore" : "ReactAfter";
        return FindVisual<FrameworkElement>(_reactRow, element => element.Name == name) ?? _reactButton;
    }

    private void ScheduleClose(bool hideSmiley)
    {
        _openTimer?.Stop();
        if (hideSmiley)
            _closeSmiley = true;
        if (_closeTimer is { IsEnabled: true })
            return;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _closeTimer.Tick += (_, _) =>
        {
            if (PointerInReactZone() || PointerOnReactionPopup())
                return;

            _closeTimer.Stop();
            var hide = _closeSmiley;
            _closeSmiley = false;
            if (hide || _reactRow?.IsMouseOver != true)
                CloseReactions();
            else
                ReactionPopup.IsOpen = false;
        };
        _closeTimer.Start();
    }

    private bool PointerInReactZone()
    {
        var anchor = ReactAnchor();
        return anchor is not null && anchor.ActualWidth > 0 && InReactZone(anchor, Mouse.GetPosition(anchor));
    }

    private bool PointerOnReactionPopup()
    {
        if (!ReactionPopup.IsOpen || ReactionPopup.Child is not FrameworkElement child || child.ActualWidth <= 0)
            return false;

        var point = Mouse.GetPosition(child);
        return point.X >= 0 && point.Y >= 0 && point.X <= child.ActualWidth && point.Y <= child.ActualHeight;
    }

    private void CloseReactions()
    {
        _openTimer?.Stop();
        _closeTimer?.Stop();
        _closeSmiley = false;
        ReactionPopup.IsOpen = false;
        if (_reactMessage is not null)
            _reactMessage.ShowReact = false;
        _reactMessage = null;
        _reactRow = null;
        _reactButton = null;
    }
}
