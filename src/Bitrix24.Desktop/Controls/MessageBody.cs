using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Navigation;
using System.Windows.Threading;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Bitrix24.Desktop.Controls;

public class MessageBody : RichTextBox
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(MessageBody),
        new PropertyMetadata("", (sender, _) => ((MessageBody)sender).Rebuild()));

    private static readonly System.Windows.Media.FontFamily CodeFont = new("Consolas");

    private readonly List<Segment> _segments = new();
    private readonly Popup _quoteCloud;
    private string _copyText = "";
    private string _quoteSlice = "";
    private int _block;
    private int _measuring;
    private Size _measured;
    private Window? _hookedWindow;
    private ScrollViewer? _hookedScroll;
    private string _shownQuote = "";

    public MessageBody()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        AcceptsTab = false;
        SpellCheck.IsEnabled = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Cursor = Cursors.IBeam;
        Focusable = true;
        IsDocumentEnabled = true;
        IsInactiveSelectionHighlightEnabled = true;
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));
        SelectionBrush = Freeze(Color.FromRgb(0x00, 0xAC, 0xE3));
        SelectionOpacity = 0.35;
        CaretBrush = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = EmptyDocument();
        BuildMenu();
        _quoteCloud = CreateQuoteCloud();
        Loaded += OnQuoteLoaded;
        Unloaded += OnQuoteUnloaded;
        DataContextChanged += OnMessageContext;
    }

    private void OnMessageContext(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is MessageItem item && item.Text != Message)
            Message = item.Text;
        _measured = new Size(0, 0);
        InvalidateMeasure();
    }

    public static readonly RoutedEvent QuoteRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(QuoteRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(MessageBody));

    public string PendingQuote => _quoteSlice;

    public event RoutedEventHandler QuoteRequested
    {
        add => AddHandler(QuoteRequestedEvent, value);
        remove => RemoveHandler(QuoteRequestedEvent, value);
    }

    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(
        nameof(CanEdit),
        typeof(bool),
        typeof(MessageBody),
        new PropertyMetadata(false, (sender, _) => ((MessageBody)sender).BuildMenu()));

    public static readonly RoutedEvent EditRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(EditRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(MessageBody));

    public bool CanEdit
    {
        get => (bool)GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }

    public event RoutedEventHandler EditRequested
    {
        add => AddHandler(EditRequestedEvent, value);
        remove => RemoveHandler(EditRequestedEvent, value);
    }

    private void BuildMenu()
    {
        var copy = new MenuItem { Header = "Копировать" };
        copy.Click += (_, _) =>
        {
            var text = Selection.IsEmpty ? FullSlice() : SelectedSlice();
            if (text.Length == 0)
                return;

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ClipboardText.Set(text)));
        };
        var menu = new ContextMenu { Items = { copy } };
        if (CanEdit)
        {
            var edit = new MenuItem { Header = "Редактировать" };
            edit.Click += (_, _) => RaiseEvent(new RoutedEventArgs(EditRequestedEvent));
            menu.Items.Add(edit);
        }

        ContextMenu = menu;
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Copy(selectionOnly: true);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        var slice = SelectedSlice();
        if (slice.Length == 0 || string.IsNullOrWhiteSpace(slice))
        {
            HideQuote();
            return;
        }

        _quoteSlice = slice;
        PlaceQuoteCloud();
    }

    private void OnQuoteLoaded(object sender, RoutedEventArgs e)
    {
        UnhookQuote();
        _hookedWindow = Window.GetWindow(this);
        if (_hookedWindow is not null)
        {
            _hookedWindow.Deactivated += HideQuoteWhenLeft;
            _hookedWindow.LocationChanged += HideQuoteOnLeave;
            _hookedWindow.StateChanged += HideQuoteOnLeave;
            _hookedWindow.SizeChanged += HideQuoteOnLeave;
            _hookedWindow.PreviewMouseDown += HideQuoteOnMouseDown;
            _hookedWindow.PreviewMouseWheel += HideQuoteOnWheel;
            _hookedWindow.PreviewKeyDown += HideQuoteOnKey;
        }

        if (Application.Current is not null)
            Application.Current.Deactivated += HideQuoteWhenLeft;

        _hookedScroll = FindScroll();
        if (_hookedScroll is not null)
            _hookedScroll.ScrollChanged += HideQuoteOnScroll;
    }

    private void OnQuoteUnloaded(object sender, RoutedEventArgs e)
    {
        HideQuote();
        UnhookQuote();
    }

    private void UnhookQuote()
    {
        if (_hookedWindow is not null)
        {
            _hookedWindow.Deactivated -= HideQuoteWhenLeft;
            _hookedWindow.LocationChanged -= HideQuoteOnLeave;
            _hookedWindow.StateChanged -= HideQuoteOnLeave;
            _hookedWindow.SizeChanged -= HideQuoteOnLeave;
            _hookedWindow.PreviewMouseDown -= HideQuoteOnMouseDown;
            _hookedWindow.PreviewMouseWheel -= HideQuoteOnWheel;
            _hookedWindow.PreviewKeyDown -= HideQuoteOnKey;
            _hookedWindow = null;
        }

        if (Application.Current is not null)
            Application.Current.Deactivated -= HideQuoteWhenLeft;

        if (_hookedScroll is not null)
        {
            _hookedScroll.ScrollChanged -= HideQuoteOnScroll;
            _hookedScroll = null;
        }
    }

    private void HideQuoteOnLeave(object? sender, EventArgs e) => HideQuote();

    private void HideQuoteWhenLeft(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(HideQuote, DispatcherPriority.Input);
    }

    private void HideQuoteOnMouseDown(object sender, MouseButtonEventArgs e) => HideQuote();

    private void HideQuoteOnWheel(object sender, MouseWheelEventArgs e) => HideQuote();

    private void HideQuoteOnKey(object sender, KeyEventArgs e) => HideQuote();

    private void HideQuoteOnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0)
            return;

        HideQuote();
    }

    private ScrollViewer? FindScroll()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is ScrollViewer viewer)
                return viewer;

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void HideQuote()
    {
        _quoteSlice = "";
        _quoteCloud.IsOpen = false;
    }

    private Popup CreateQuoteCloud()
    {
        var cloud = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Data = Geometry.Parse(
                "M40 15c-10.493 0-19 8.507-19 19s8.507 19 19 19l6 6 6-6c10.493 0 19-8.507 19-19s-8.507-19-19-19z"),
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 1,
                Opacity = 0.16,
                Color = Colors.Black
            },
            IsHitTestVisible = false
        };
        var ink = Freeze(Color.FromRgb(0xBD, 0xC1, 0xC6));
        var inkHover = Freeze(Color.FromRgb(0x95, 0x9C, 0xA4));
        var marks = new System.Windows.Shapes.Path
        {
            Fill = ink,
            Data = Geometry.Parse(
                "M45.505 29.411A1.41 1.41 0 0 0 44.095 28H40.91a1.41 1.41 0 0 0-1.41 1.411v3.126"
                + "c0 .78.631 1.411 1.41 1.411h2.39c0 .477-.008 1.746-.464 2.419-.492.727-1.326 1.17-1.857 1.394"
                + "a.79.79 0 0 0-.498.705l-.01 1.222a.283.283 0 0 0 .345.285c.796-.185 2.739-.752 3.537-2.02"
                + "c1.017-1.615 1.152-3.656 1.152-4.497zm6.995 0A1.41 1.41 0 0 0 51.09 28h-3.185"
                + "a1.41 1.41 0 0 0-1.41 1.411v3.126c0 .78.631 1.411 1.41 1.411h2.39c0 .477-.008 1.746-.464 2.419"
                + "-.492.727-1.326 1.17-1.857 1.394a.79.79 0 0 0-.498.705l-.01 1.222a.283.283 0 0 0 .345.285"
                + "c.796-.185 2.739-.752 3.537-2.02 1.017-1.615 1.152-3.656 1.152-4.497z"),
            IsHitTestVisible = false
        };
        var sheet = new Canvas
        {
            Width = 92,
            Height = 86,
            Margin = new Thickness(-16, -15, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        sheet.Children.Add(cloud);
        sheet.Children.Add(marks);
        var face = new Border
        {
            Width = 60,
            Height = 44,
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = sheet,
            Cursor = Cursors.Hand
        };
        face.MouseEnter += (_, _) => marks.Fill = inkHover;
        face.MouseLeave += (_, _) => marks.Fill = ink;
        face.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            var text = _shownQuote;
            HideQuote();
            Selection.Select(Selection.End, Selection.End);
            if (text.Length == 0)
                return;
            _quoteSlice = text;
            RaiseEvent(new RoutedEventArgs(QuoteRequestedEvent, this));
        };
        return new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Child = face
        };
    }

    private void PlaceQuoteCloud()
    {
        Rect rect;
        try
        {
            rect = Selection.End.GetCharacterRect(LogicalDirection.Backward);
        }
        catch (InvalidOperationException)
        {
            HideQuote();
            return;
        }

        if (rect.IsEmpty)
        {
            HideQuote();
            return;
        }

        _shownQuote = _quoteSlice;
        _quoteCloud.HorizontalOffset = rect.Right - 60;
        _quoteCloud.VerticalOffset = rect.Top - 48;
        _quoteCloud.IsOpen = true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (DataContext is MessageItem item && item.Text != Message)
            Message = item.Text;

        if (_measuring > 0)
            return _measured;

        _measuring++;
        try
        {
            var limit = constraint.Width;
            if (double.IsNaN(limit) || double.IsInfinity(limit) || limit < 1)
                limit = 320;

            SetPage(limit);
            if (VisualTreeHelper.GetChildrenCount(this) == 0 || VisualTreeHelper.GetChild(this, 0) is not UIElement child)
            {
                _measured = new Size(0, 0);
                return _measured;
            }

            child.Measure(new Size(limit, double.PositiveInfinity));
            var height = child.DesiredSize.Height;
            var estimate = EstimateWidth();
            var width = estimate > 0 ? Math.Min(limit, Math.Ceiling(estimate)) : limit;
            if (width < limit - 1)
            {
                var fitted = FitLine(child, width, limit, height);
                if (estimate > 0 && estimate < limit - 40 && fitted >= limit - 1)
                    FileLog.Write($"пузырь текст «{Snippet(Message)}» оценка {estimate:0.#} не берёт потолок {fitted:0.#}");
                else
                    width = fitted;
            }

            _measured = new Size(width, child.DesiredSize.Height);
            return _measured;
        }
        finally
        {
            _measuring--;
        }
    }

    private double FitLine(UIElement child, double width, double limit, double height)
    {
        SetPage(width);
        child.Measure(new Size(width, double.PositiveInfinity));
        if (child.DesiredSize.Height <= height + 1)
            return width;

        var low = width;
        var high = limit;
        for (var step = 0; step < 12 && high - low > 1; step++)
        {
            var mid = Math.Ceiling((low + high) / 2);
            SetPage(mid);
            child.Measure(new Size(mid, double.PositiveInfinity));
            if (child.DesiredSize.Height > height + 1)
                low = mid;
            else
                high = mid;
        }

        SetPage(high);
        child.Measure(new Size(high, double.PositiveInfinity));
        return high;
    }

    private void Rebuild()
    {
        HideQuote();
        _measured = new Size(0, 0);
        _segments.Clear();
        _block = 0;
        var pieces = QuoteText.Parse(Message);
        _copyText = CopyText(pieces);
        var single = MessageWidth.IsSingleEmoji(pieces, EmojiCatalog.Shared.Has);
        var document = EmptyDocument();
        var ink = Foreground ?? Brushes.Black;
        var wrote = false;
        foreach (var piece in pieces)
        {
            if (piece is MessageQuote quote)
            {
                document.Blocks.Add(QuoteBlock(quote, wrote));
                wrote = true;
                continue;
            }

            if (piece is MessageCode code)
            {
                if (code.Text.Length == 0)
                    continue;

                document.Blocks.Add(CodeParagraph(code.Text, wrote));
                wrote = true;
                continue;
            }

            if (piece is not MessageText plain || plain.Text.Length == 0)
                continue;

            var body = plain.Text;
            var emoji = MessageWidth.InlineEmoji;
            var line = 20d;
            if (single)
            {
                body = plain.Text.Trim();
                emoji = MessageWidth.EmojiSize(true, NativePixels(body));
                line = double.NaN;
            }

            var paragraph = BodyParagraph(ink, 15, line);
            if (wrote)
                paragraph.Margin = new Thickness(0, 4, 0, 0);
            _block++;
            Fill(paragraph, body, emoji, ink);
            document.Blocks.Add(paragraph);
            wrote = true;
        }

        Document = document;
        InvalidateMeasure();
    }

    private Block QuoteBlock(MessageQuote quote, bool gapBefore)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, gapBefore ? 4 : 0, 0, 4)
        };
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var row = new TableRow();
        var cell = new TableCell
        {
            Background = Brush("QuoteWash", "#F8FAFB"),
            BorderBrush = Brush("QuoteName", "#0075FF"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 6, 8, 6)
        };

        var nameBrush = Brush("QuoteName", "#0075FF");
        _block++;
        var name = new Paragraph { Margin = new Thickness(0, 0, 0, 2), LineHeight = 17 };
        if (quote.Author.Length > 0)
        {
            var author = new Run(quote.Author)
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = nameBrush
            };
            name.Inlines.Add(author);
            Remember(author, quote.Author);
        }

        if (quote.When.Length > 0)
        {
            var when = new Run((quote.Author.Length > 0 ? "  " : "") + quote.When)
            {
                FontSize = 11,
                Foreground = nameBrush
            };
            name.Inlines.Add(when);
            Remember(when, (quote.Author.Length > 0 ? "  " : "") + quote.When);
        }

        if (name.Inlines.Count > 0)
            cell.Blocks.Add(name);

        if (quote.Body.Length > 0)
            AddQuoteBody(cell, quote.Body);

        row.Cells.Add(cell);
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        return table;
    }

    private void AddQuoteBody(TableCell cell, string body)
    {
        var ink = Brush("QuoteInk", "#828B95");
        var wrote = false;
        foreach (var segment in CodeText.Split(body))
        {
            if (segment.IsCode)
            {
                if (segment.Text.Length == 0)
                    continue;

                cell.Blocks.Add(CodeParagraph(segment.Text, wrote));
                wrote = true;
                continue;
            }

            var text = segment.Text.Trim('\n');
            if (text.Length == 0)
                continue;

            _block++;
            var paragraph = BodyParagraph(ink, 13, 17);
            if (wrote)
                paragraph.Margin = new Thickness(0, 4, 0, 0);
            Fill(paragraph, text, MessageWidth.QuoteEmoji, ink);
            cell.Blocks.Add(paragraph);
            wrote = true;
        }
    }

    private Block CodeParagraph(string text, bool gapBefore)
    {
        _block++;
        var paragraph = BodyParagraph(Foreground ?? Brushes.Black, 13, 18);
        paragraph.FontFamily = CodeFont;
        paragraph.Background = Brush("CodeWash", "#E8EEF2");
        paragraph.Padding = new Thickness(8, 6, 8, 6);
        paragraph.Margin = new Thickness(0, gapBefore ? 4 : 0, 0, 4);
        AddLiteral(paragraph, text);
        return paragraph;
    }

    private void AddLiteral(Paragraph paragraph, string text)
    {
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                var line = new LineBreak();
                paragraph.Inlines.Add(line);
                Remember(line, "\n");
            }

            if (parts[index].Length == 0)
                continue;

            var run = new Run(parts[index]) { Foreground = paragraph.Foreground, FontFamily = CodeFont };
            paragraph.Inlines.Add(run);
            Remember(run, parts[index]);
        }
    }

    private void Fill(Paragraph paragraph, string text, double emojiSize, Brush foreground)
    {
        foreach (var piece in EmojiPieces.Split(text, EmojiCatalog.Shared.Has))
        {
            if (piece.CodePoint == 0)
                AddPlain(paragraph, piece.Text, foreground);
            else
                AddEmoji(paragraph, piece, emojiSize);
        }
    }

    private void AddPlain(Paragraph paragraph, string text, Brush foreground)
    {
        foreach (var styled in FormatText.Split(text))
        {
            foreach (var piece in LinkText.Split(styled.Text))
                AddChunk(paragraph, piece.Text, piece.Target, foreground, styled.Style);
        }
    }

    private void AddChunk(Paragraph paragraph, string text, string? target, Brush foreground, TextStyle style)
    {
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                var line = new LineBreak();
                paragraph.Inlines.Add(line);
                Remember(line, "\n");
            }

            if (parts[index].Length == 0)
                continue;

            if (target is null)
                AddRuns(paragraph, parts[index], foreground, style);
            else
                AddLink(paragraph, parts[index], target, style);
        }
    }

    private void AddRuns(Paragraph paragraph, string text, Brush foreground, TextStyle style)
    {
        var run = new Run(text) { Foreground = foreground };
        ApplyStyle(run, style);
        paragraph.Inlines.Add(run);
        Remember(run, text);
    }

    private void AddLink(Paragraph paragraph, string label, string target, TextStyle style)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            AddRuns(paragraph, label, foreground: Foreground ?? Brushes.Black, style);
            return;
        }

        var run = new Run(label);
        ApplyStyle(run, style);
        var link = new Hyperlink(run)
        {
            NavigateUri = uri,
            Foreground = Brush("LinkInk", "#005BBB"),
            Cursor = Cursors.Hand
        };
        if (!style.Underline)
            link.TextDecorations = TextDecorations.Underline;
        if (!string.Equals(label, target, StringComparison.Ordinal)
            && !string.Equals("https://" + label, target, StringComparison.OrdinalIgnoreCase)
            && !string.Equals("http://" + label, target, StringComparison.OrdinalIgnoreCase))
            link.ToolTip = target;
        paragraph.Inlines.Add(link);
        Remember(run, label);
    }

    private static void ApplyStyle(Run run, TextStyle style)
    {
        if (style.Bold)
            run.FontWeight = FontWeights.Bold;
        if (style.Italic)
            run.FontStyle = FontStyles.Italic;

        TextDecorationCollection? decorations = null;
        if (style.Underline)
            decorations = TextDecorations.Underline;
        if (style.Strike)
        {
            decorations ??= new TextDecorationCollection();
            foreach (var strike in TextDecorations.Strikethrough)
                decorations.Add(strike);
        }

        if (decorations is not null)
            run.TextDecorations = decorations;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is null)
            return;
        if (e.Uri.Scheme != Uri.UriSchemeHttp && e.Uri.Scheme != Uri.UriSchemeHttps)
            return;

        var url = e.Uri.OriginalString;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = e.Uri.AbsoluteUri;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private void AddEmoji(Paragraph paragraph, EmojiPiece piece, double size)
    {
        var picture = EmojiCatalog.Shared.Picture(piece.CodePoint, size);
        if (picture == null)
        {
            AddPlain(paragraph, piece.Text, Foreground ?? Brushes.Black);
            return;
        }

        picture.VerticalAlignment = VerticalAlignment.Center;
        var holder = new InlineUIContainer(picture)
        {
            BaselineAlignment = BaselineAlignment.Center,
            Tag = piece.Text
        };
        paragraph.Inlines.Add(holder);
        Remember(holder, piece.Text);
    }

    private void Remember(TextElement element, string text) =>
        _segments.Add(new Segment(element, text, _block));

    private static bool SliceOf(Segment segment, TextPointer start, TextPointer end, out string chunk)
    {
        chunk = "";
        if (segment.Element is Run run)
        {
            if (end.CompareTo(run.ContentStart) <= 0 || start.CompareTo(run.ContentEnd) >= 0)
                return false;

            var from = start.CompareTo(run.ContentStart) > 0 ? start : run.ContentStart;
            var to = end.CompareTo(run.ContentEnd) < 0 ? end : run.ContentEnd;
            var offset = run.ContentStart.GetOffsetToPosition(from);
            var length = from.GetOffsetToPosition(to);
            if (offset < 0 || length <= 0 || offset > run.Text.Length)
                return false;

            var take = Math.Min(length, run.Text.Length - offset);
            if (take <= 0)
                return false;

            chunk = run.Text.Substring(offset, take);
            return true;
        }

        var elementStart = segment.Element.ElementStart;
        var elementEnd = segment.Element.ElementEnd;
        if (end.CompareTo(elementStart) <= 0 || start.CompareTo(elementEnd) >= 0)
            return false;

        chunk = segment.Text;
        return true;
    }

    private static string CopyText(IReadOnlyList<MessagePiece> pieces)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            var chunk = piece switch
            {
                MessageQuote quote => QuoteCopy(quote),
                MessageCode code => code.Text,
                MessageText plain => LinkText.Visible(plain.Text.Trim('\n')),
                _ => ""
            };
            if (chunk.Length == 0)
                continue;
            if (builder.Length > 0)
                builder.Append("\n");
            builder.Append(chunk);
        }

        return builder.ToString();
    }

    private static string QuoteCopy(MessageQuote quote)
    {
        var builder = new StringBuilder();
        if (quote.Author.Length > 0)
            builder.Append(quote.Author);
        if (quote.When.Length > 0)
        {
            if (builder.Length > 0)
                builder.Append("  ");
            builder.Append(quote.When);
        }

        if (quote.Body.Length == 0)
            return builder.ToString();
        if (builder.Length > 0)
            builder.Append('\n');
        builder.Append(LinkText.Visible(CodeText.Visible(quote.Body)));
        return builder.ToString();
    }

    private void Copy(bool selectionOnly)
    {
        var text = selectionOnly ? SelectedSlice() : FullSlice();
        if (text.Length == 0)
            return;

        ClipboardText.Set(text);
    }

    private string FullSlice() => _copyText;

    private string SelectedSlice()
    {
        if (Selection.IsEmpty)
            return "";

        var start = Selection.Start;
        var end = Selection.End;
        if (start.CompareTo(end) > 0)
            (start, end) = (end, start);

        var builder = new StringBuilder();
        var seen = -1;
        foreach (var segment in _segments)
        {
            if (!SliceOf(segment, start, end, out var chunk))
                continue;

            if (seen >= 0 && segment.Block != seen)
                builder.Append('\n');
            seen = segment.Block;
            builder.Append(chunk);
        }

        return builder.ToString();
    }

    private void SetPage(double width)
    {
        if (Document == null)
            return;

        var page = Math.Max(width, 1);
        if (!double.IsNaN(Document.PageWidth) && Math.Abs(Document.PageWidth - page) <= 0.5)
            return;

        Document.PageWidth = page;
        Document.ColumnWidth = page;
    }

    private double EstimateWidth() => BubbleMeasure.Content(Message);

    private static string Snippet(string text)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 32 ? flat : flat[..32];
    }

    private static int NativePixels(string text)
    {
        var glyphs = EmojiPieces.Split(text, EmojiCatalog.Shared.Has);
        if (glyphs.Count != 1 || glyphs[0].CodePoint == 0)
            return 0;

        return EmojiCatalog.Shared.PixelSize(glyphs[0].CodePoint);
    }

    private static Paragraph BodyParagraph(Brush foreground, double size, double lineHeight)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            FontSize = size,
            Foreground = foreground,
            LineStackingStrategy = LineStackingStrategy.MaxHeight
        };
        if (!double.IsNaN(lineHeight))
            paragraph.LineHeight = lineHeight;
        return paragraph;
    }

    private static FlowDocument EmptyDocument() =>
        new()
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

    private static Brush Brush(string key, string hex)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        return Freeze((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Segment
    {
        public Segment(TextElement element, string text, int block)
        {
            Element = element;
            Text = text;
            Block = block;
        }

        public TextElement Element { get; }
        public string Text { get; }
        public int Block { get; }
    }
}
