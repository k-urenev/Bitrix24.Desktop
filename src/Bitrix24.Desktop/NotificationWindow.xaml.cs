using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop;

public partial class NotificationWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private const int VisibleLimit = 3;
    private const int LifeSeconds = 15;

    private readonly ObservableCollection<NotificationCard> _cards = new();
    private readonly DispatcherTimer _timer;

    public NotificationWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
        ShowActivated = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Expire();
        _timer.Start();
        SourceInitialized += (_, _) => ApplyNoActivate();
        _cards.CollectionChanged += (_, _) =>
        {
            if (_cards.Count == 0 && IsVisible)
                Hide();
            else
                Dispatcher.BeginInvoke(Place);
        };
    }

    public event EventHandler<NotificationCard>? CardChosen;

    public void DismissAll() => _cards.Clear();

    public void DismissDialog(string dialogId)
    {
        for (var index = _cards.Count - 1; index >= 0; index--)
        {
            var card = _cards[index];
            if (card.Kind == NotificationKind.Message
                && string.Equals(card.DialogId, dialogId, StringComparison.Ordinal))
                _cards.RemoveAt(index);
        }
    }

    public void Push(AppNotification notification)
    {
        while (_cards.Count >= VisibleLimit)
            _cards.RemoveAt(_cards.Count - 1);

        var cursor = ReadCursor();
        var card = NotificationCard.From(notification);
        card.AnchorX = cursor.X;
        card.AnchorY = cursor.Y;
        _cards.Insert(0, card);
        if (!IsVisible)
            Show();
        UpdateLayout();
        Place();
    }

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        var cursor = ReadCursor();
        for (var index = _cards.Count - 1; index >= 0; index--)
        {
            var card = _cards[index];
            if (!card.MouseMoved && (cursor.X != card.AnchorX || cursor.Y != card.AnchorY))
                card.MouseMoved = true;

            if (card.ExpiresAt > now)
                continue;

            if (!card.Prolonged && !card.MouseMoved)
            {
                card.Prolonged = true;
                card.ExpiresAt = now.AddSeconds(LifeSeconds);
                continue;
            }

            _cards.RemoveAt(index);
        }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void OnCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Button>(source) is not null)
            return;
        if (sender is not FrameworkElement { DataContext: NotificationCard card })
            return;

        e.Handled = true;
        CardChosen?.Invoke(this, card);
        _cards.Remove(card);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseActivate)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(MaNoActivate);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotificationCard card })
            _cards.Remove(card);
        e.Handled = true;
    }

    private void ApplyNoActivate()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style.ToInt64() | WsExNoActivate | WsExToolWindow));
            HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static CursorPoint ReadCursor()
    {
        return GetCursorPos(out var point) ? point : new CursorPoint { X = int.MinValue, Y = int.MinValue };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(handle, index, value) : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
}

public sealed class NotificationCard
{
    public NotificationKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? DialogId { get; init; }
    public string? ExternalUrl { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Prolonged { get; set; }
    public bool MouseMoved { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }

    public static NotificationCard From(AppNotification notification) => new()
    {
        Kind = notification.Kind,
        Title = notification.Title,
        Body = notification.Body,
        DialogId = notification.DialogId,
        ExternalUrl = notification.ExternalUrl,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(15)
    };
}
