using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace Bitrix24.Desktop.Controls;

public class ReceiptMark : FrameworkElement
{
    private const double SentWidth = 11;
    private const double ViewedWidth = 14;
    private const double MarkHeight = 11;

    private static readonly Geometry SentMark = Make(
        "M4.514 8.605a.3.3 0 0 1-.419 0L1.686 6.258a.4.4 0 0 1 0-.573l.447-.435a.4.4 0 0 1 .558 0l1.614 1.573 4.85-4.747a.4.4 0 0 1 .56-.001l.445.435a.4.4 0 0 1 0 .572z");

    private static readonly Geometry ViewedMark = Make(
        "M7.346 8.609a.3.3 0 0 1-.416.004l-.943-.892a.4.4 0 0 1-.005-.577L6.44 6.7a.4.4 0 0 1 .542-.014l.159.139 4.85-4.748a.4.4 0 0 1 .56 0l.445.434a.4.4 0 0 1 0 .573zm-4.141-.003a.3.3 0 0 1-.419 0L.377 6.258a.4.4 0 0 1 0-.572l.447-.435a.4.4 0 0 1 .558 0l1.614 1.573 4.85-4.748a.4.4 0 0 1 .56 0l.445.434a.4.4 0 0 1 .001.573z");

    public static readonly DependencyProperty ViewedProperty = DependencyProperty.Register(
        nameof(Viewed),
        typeof(bool),
        typeof(ReceiptMark),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush DefaultFill = CreateFill();

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(ReceiptMark),
        new FrameworkPropertyMetadata(DefaultFill, FrameworkPropertyMetadataOptions.AffectsRender));

    public ReceiptMark()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public bool Viewed
    {
        get => (bool)GetValue(ViewedProperty);
        set => SetValue(ViewedProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size available) =>
        new(Viewed ? ViewedWidth : SentWidth, MarkHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Fill is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var viewWidth = Viewed ? ViewedWidth : SentWidth;
        var scale = Math.Min(ActualWidth / viewWidth, ActualHeight / MarkHeight);
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(Fill, null, Viewed ? ViewedMark : SentMark);
        drawingContext.Pop();
    }

    private static Geometry Make(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFill()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xAD, 0xB4));
        brush.Freeze();
        return brush;
    }
}
