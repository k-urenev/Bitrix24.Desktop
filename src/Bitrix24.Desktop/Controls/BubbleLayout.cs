using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.ViewModels;
using Size = System.Windows.Size;

namespace Bitrix24.Desktop.Controls;

public class BubbleLayout : Panel
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill",
        typeof(bool),
        typeof(BubbleLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FloorProperty = DependencyProperty.RegisterAttached(
        "Floor",
        typeof(bool),
        typeof(BubbleLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow",
        typeof(bool),
        typeof(BubbleLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double _cap;
    private double _body;
    private double _other;
    private double _floor;
    private double _grow;
    private double _chosen;
    private int _logId = -1;
    private int _logWidth = int.MinValue;
    private bool _logBad;

    public BubbleLayout()
    {
        DataContextChanged += (_, _) =>
        {
            IsChecked = false;
            _logId = -1;
            _logWidth = int.MinValue;
            InvalidateMeasure();
        };
    }

    public bool IsChecked { get; private set; }

    public static bool GetFill(DependencyObject element) => (bool)element.GetValue(FillProperty);

    public static void SetFill(DependencyObject element, bool value) => element.SetValue(FillProperty, value);

    public static bool GetFloor(DependencyObject element) => (bool)element.GetValue(FloorProperty);

    public static void SetFloor(DependencyObject element, bool value) => element.SetValue(FloorProperty, value);

    public static bool GetGrow(DependencyObject element) => (bool)element.GetValue(GrowProperty);

    public static void SetGrow(DependencyObject element, bool value) => element.SetValue(GrowProperty, value);

    protected override Size MeasureOverride(Size constraint)
    {
        _cap = double.IsInfinity(constraint.Width) ? 420 : Math.Max(0, constraint.Width);
        var item = DataContext as MessageItem;
        _body = item is null || item.HasFiles ? 0 : BubbleMeasure.Inner(item);
        _other = 0;
        _floor = item is null ? 0 : BubbleMeasure.Time(item);
        _grow = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (Skip(child, item))
                continue;

            var margin = MarginOf(child);
            if (GetGrow(child))
            {
                if (item is { HasReactions: true })
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var width = child.DesiredSize.Width;
                    if (!double.IsNaN(width) && !double.IsInfinity(width))
                        _grow = Math.Max(_grow, width + margin.Left + margin.Right);
                }

                continue;
            }

            if (GetFill(child) || child is MessageBody)
                continue;

            var inner = Math.Max(0, _cap - margin.Left - margin.Right);
            child.Measure(new Size(inner, double.PositiveInfinity));
            _other = Math.Max(_other, child.DesiredSize.Width + margin.Left + margin.Right);
        }

        var want = Math.Max(_body, Math.Max(_other, Math.Max(_floor, _grow)));
        if (_grow > 0)
            want = Math.Max(want, Math.Ceiling(Math.Min(_grow, _cap)));
        _chosen = Math.Min(_cap, Math.Max(0, want));
        NoteMeasure();
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (Skip(child, item))
                continue;

            var margin = MarginOf(child);
            var inner = Math.Max(0, _chosen - margin.Left - margin.Right);
            child.Measure(new Size(inner, double.PositiveInfinity));
            height += child.DesiredSize.Height + margin.Top + margin.Bottom;
        }

        return new Size(_chosen, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        var item = DataContext as MessageItem;
        foreach (UIElement child in InternalChildren)
        {
            if (Skip(child, item))
                continue;

            var margin = MarginOf(child);
            var width = Math.Max(0, finalSize.Width - margin.Left - margin.Right);
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(margin.Left, y + margin.Top, width, height));
            y += height + margin.Top + margin.Bottom;
        }

        NoteArrange(finalSize.Width);
        return finalSize;
    }

    private void NoteMeasure()
    {
        if (DataContext is not MessageItem item)
            return;

        if (item.HasText && !item.HasFiles && _body <= 1)
            return;

        var padding = Parent is Border border ? border.Padding.Left + border.Padding.Right : 0;
        Write(item, _chosen, _chosen + padding);
        IsChecked = true;
    }

    private void NoteArrange(double inner)
    {
        if (DataContext is not MessageItem item || !IsChecked)
            return;

        var padding = Parent is Border border ? border.Padding.Left + border.Padding.Right : 0;
        if (inner <= _chosen + 6)
            return;

        Write(item, inner, inner + padding);
    }

    private void Write(MessageItem item, double inner, double outer)
    {
        var padding = Parent is Border border ? border.Padding.Left + border.Padding.Right : 0;
        var limit = _cap + padding;
        var content = Math.Max(_body, Math.Max(_other, Math.Max(_floor, _grow)));
        var bad = !item.HasFiles
            && _body > 1
            && _body < _cap - 40
            && _grow < _cap - 1
            && (inner > content + 6 || outer > limit - 1);
        var width = (int)Math.Round(outer);
        if (IsChecked && _logId == item.Id && _logWidth == width && _logBad == bad)
            return;

        _logId = item.Id;
        _logWidth = width;
        _logBad = bad;
        var snippet = Snippet(item.Text);
        FileLog.Write(
            string.Format(
                CultureInfo.InvariantCulture,
                "пузырь {0} внешняя {1:0.#} внутренняя {2:0.#} потолок {3:0.#} текст {4:0.#} время {5:0.#} реакции {6:0.#} сверка {7} «{8}»",
                item.Id,
                outer,
                inner,
                limit,
                _body,
                _floor,
                _grow,
                bad ? "нет" : "да",
                snippet));
        if (!bad)
            return;

        FileLog.Write(
            string.Format(
                CultureInfo.InvariantCulture,
                "пузырь {0} короткий текст у потолка: пузырь {1:0.#} при тексте {2:0.#} «{3}»",
                item.Id,
                outer,
                _body,
                snippet));
    }

    private static bool Skip(UIElement child, MessageItem? item)
    {
        if (item is null)
            return child.Visibility == Visibility.Collapsed;
        if (child is MessageBody)
            return !item.HasText;
        if (child is ItemsControl)
            return !item.HasFiles;
        if (child is TextBlock)
            return !item.ShowAuthor;
        if (GetFill(child) && child is Border)
            return !item.HasLinkPreview;
        return child.Visibility == Visibility.Collapsed;
    }

    private static string Snippet(string text)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 32 ? flat : flat[..32];
    }

    private static Thickness MarginOf(UIElement element) =>
        element is FrameworkElement { Margin: var margin } ? margin : new Thickness();
}
