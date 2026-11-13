using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Controls;

public class EmojiBox : RichTextBox
{
    public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
        nameof(PlainText),
        typeof(string),
        typeof(EmojiBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPlainTextChanged));

    private bool _internal;

    public EmojiBox()
    {
        SpellCheck.IsEnabled = false;
        AcceptsReturn = true;
        TextChanged += OnTextChanged;
        DataObject.AddPastingHandler(this, OnPasting);
        Show("");
    }

    internal Func<string>? ClipboardImageName { get; set; }

    internal event EventHandler<ClipboardPaste>? ImagesPasted;

    public string PlainText
    {
        get => (string)GetValue(PlainTextProperty);
        set => SetValue(PlainTextProperty, value);
    }

    public void InsertEmoji(string character)
    {
        if (string.IsNullOrEmpty(character))
            return;

        var plain = ReadPlain();
        var caret = GetOffset(CaretPosition);
        if (caret < 0 || caret > plain.Length)
            caret = plain.Length;

        plain = plain.Insert(caret, character);
        Show(plain);
        SetCaretOffset(caret + character.Length);
        Push(plain);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CutSelection();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    public void PlaceCaretAtEnd() => SetCaretOffset(ReadPlain().Length);

    public void BreakLine()
    {
        var after = CaretPosition.InsertLineBreak();
        if (after != null)
            CaretPosition = after;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyPageWidth();
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var paste = ClipboardImages.Take(e.DataObject, ClipboardImageName ?? (() => "Изображение.png"));
        if (paste.Handled)
        {
            e.CancelCommand();
            ImagesPasted?.Invoke(this, paste);
            return;
        }

        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.FormatToApply = DataFormats.UnicodeText;
            return;
        }

        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.FormatToApply = DataFormats.Text;
            return;
        }

        e.CancelCommand();
    }

    private static void OnPlainTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (EmojiBox)sender;
        if (box._internal)
            return;

        var text = e.NewValue as string ?? "";
        box.Show(text);
        box.PlaceCaretAtEnd();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_internal)
            return;

        var plain = ReadPlain();
        if (HasRawEmoji())
        {
            var offset = GetOffset(CaretPosition);
            Show(plain);
            SetCaretOffset(offset);
        }

        Push(plain);
    }

    private void Show(string text)
    {
        _internal = true;
        try
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                Foreground = Foreground,
                Background = Background
            };
            var paragraph = new Paragraph { Margin = new Thickness(0), Foreground = Foreground };
            document.Blocks.Add(paragraph);
            foreach (var piece in EmojiPieces.Split(text, EmojiCatalog.Shared.Has))
            {
                if (piece.CodePoint == 0)
                    AddText(paragraph, piece.Text, Foreground);
                else
                    AddEmoji(paragraph, piece);
            }

            Document = document;
            ApplyPageWidth();
        }
        finally
        {
            _internal = false;
        }
    }

    private static void AddText(Paragraph paragraph, string text, System.Windows.Media.Brush foreground)
    {
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            if (parts[i].Length > 0)
                paragraph.Inlines.Add(new Run(parts[i]) { Foreground = foreground });
        }
    }

    private static void AddEmoji(Paragraph paragraph, EmojiPiece piece)
    {
        var picture = EmojiCatalog.Shared.Picture(piece.CodePoint, 20);
        if (picture == null)
        {
            paragraph.Inlines.Add(new Run(piece.Text) { Foreground = paragraph.Foreground });
            return;
        }

        picture.VerticalAlignment = VerticalAlignment.Center;
        paragraph.Inlines.Add(new InlineUIContainer(picture)
        {
            BaselineAlignment = BaselineAlignment.Center,
            Tag = piece.Text
        });
    }

    private bool HasRawEmoji()
    {
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph)
                continue;

            if (HasRawEmoji(paragraph.Inlines))
                return true;
        }

        return false;
    }

    private static bool HasRawEmoji(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    foreach (var piece in EmojiPieces.Split(run.Text, EmojiCatalog.Shared.Has))
                    {
                        if (piece.CodePoint != 0)
                            return true;
                    }

                    break;
                case Span span when HasRawEmoji(span.Inlines):
                    return true;
            }
        }

        return false;
    }

    private string ReadPlain()
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var block in Document.Blocks)
        {
            if (!first)
                builder.Append("\r\n");
            first = false;
            if (block is not Paragraph paragraph)
                continue;

            AppendInlines(builder, paragraph.Inlines);
        }

        return builder.ToString();
    }

    private static void AppendInlines(StringBuilder builder, InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.Append("\r\n");
                    break;
                case InlineUIContainer { Tag: string tag }:
                    builder.Append(tag);
                    break;
                case Span span:
                    AppendInlines(builder, span.Inlines);
                    break;
            }
        }
    }

    private int GetOffset(TextPointer pointer)
    {
        var offset = 0;
        var first = true;
        foreach (var block in Document.Blocks)
        {
            if (!first)
            {
                if (pointer.CompareTo(block.ContentStart) <= 0)
                    return offset;
                offset += 2;
            }

            first = false;
            if (block is not Paragraph paragraph)
                continue;

            foreach (var inline in paragraph.Inlines)
            {
                if (TryOffset(inline, pointer, ref offset))
                    return offset;
            }
        }

        return offset;
    }

    private void SetCaretOffset(int target)
    {
        var remaining = Math.Max(target, 0);
        Paragraph? last = null;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph)
                continue;

            last = paragraph;
            foreach (var inline in paragraph.Inlines)
            {
                if (PlaceCaret(inline, ref remaining))
                    return;
            }
        }

        if (last != null)
            CaretPosition = last.ContentEnd;
    }

    private static bool TryOffset(Inline inline, TextPointer pointer, ref int offset)
    {
        switch (inline)
        {
            case Run run:
                var start = run.ContentStart;
                var end = run.ContentEnd;
                if (pointer.CompareTo(start) >= 0 && pointer.CompareTo(end) <= 0)
                {
                    offset += start.GetOffsetToPosition(pointer);
                    return true;
                }

                offset += run.Text.Length;
                return false;
            case LineBreak:
                if (pointer.CompareTo(inline.ElementEnd) <= 0)
                    return true;
                offset += 2;
                return false;
            case Span span:
                foreach (var child in span.Inlines)
                {
                    if (TryOffset(child, pointer, ref offset))
                        return true;
                }

                return false;
            case InlineUIContainer container:
                var length = (container.Tag as string)?.Length ?? 0;
                if (pointer.CompareTo(inline.ElementEnd) < 0)
                    return true;
                offset += length;
                return false;
            default:
                return false;
        }
    }

    private bool PlaceCaret(Inline inline, ref int remaining)
    {
        switch (inline)
        {
            case Run run:
                if (remaining <= run.Text.Length)
                {
                    CaretPosition = run.ContentStart.GetPositionAtOffset(remaining) ?? run.ContentEnd;
                    return true;
                }

                remaining -= run.Text.Length;
                return false;
            case LineBreak:
                if (remaining <= 0)
                {
                    CaretPosition = inline.ElementStart;
                    return true;
                }

                remaining -= 2;
                return false;
            case Span span:
                foreach (var child in span.Inlines)
                {
                    if (PlaceCaret(child, ref remaining))
                        return true;
                }

                return false;
            case InlineUIContainer container:
                var length = (container.Tag as string)?.Length ?? 0;
                if (remaining <= 0)
                {
                    CaretPosition = inline.ElementStart;
                    return true;
                }

                if (remaining <= length)
                {
                    CaretPosition = inline.ElementEnd;
                    return true;
                }

                remaining -= length;
                return false;
            default:
                return false;
        }
    }

    private void Push(string plain)
    {
        if (plain == PlainText)
            return;

        _internal = true;
        try
        {
            SetCurrentValue(PlainTextProperty, plain);
        }
        finally
        {
            _internal = false;
        }
    }

    private void CopySelection()
    {
        if (Selection.IsEmpty)
            return;

        var slice = SelectedText();
        if (slice.Length > 0)
            ClipboardText.Set(slice);
    }

    private void CutSelection()
    {
        if (Selection.IsEmpty)
            return;

        var plain = ReadPlain();
        var start = GetOffset(Selection.Start);
        var end = GetOffset(Selection.End);
        if (end < start)
            (start, end) = (end, start);
        start = Math.Clamp(start, 0, plain.Length);
        end = Math.Clamp(end, 0, plain.Length);
        if (start == end)
            return;

        ClipboardText.Set(plain[start..end]);
        plain = plain.Remove(start, end - start);
        Show(plain);
        SetCaretOffset(start);
        Push(plain);
    }

    private string SelectedText()
    {
        var plain = ReadPlain();
        var start = GetOffset(Selection.Start);
        var end = GetOffset(Selection.End);
        if (end < start)
            (start, end) = (end, start);
        start = Math.Clamp(start, 0, plain.Length);
        end = Math.Clamp(end, 0, plain.Length);
        return start == end ? "" : plain[start..end];
    }

    private void ApplyPageWidth()
    {
        var width = ActualWidth - Padding.Left - Padding.Right;
        if (width > 1 && Document != null)
            Document.PageWidth = width;
    }
}
