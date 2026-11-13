using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace Bitrix24.Desktop.Controls;

public class ReactionMark : FrameworkElement
{
    private const double MarkWidth = 13;
    private const double MarkHeight = 10;

    private static readonly Geometry Heart = Make(
        "M2.467.586C1.558 1.07.85 1.981.834 2.974c-.004.224.01.448.023.672.016.264.073.426.2.809.128.382.493.995.833 1.436.247.32.527.611.822.89.45.426 3.808 2.974 3.808 2.974s3.47-2.688 3.86-3.065c.312-.302.605-.62.856-.974.37-.526.776-1.108.868-1.74q.047-.339.059-.681a3.3 3.3 0 0 0-.09-.99c-.194-.711-.787-1.267-1.426-1.652a2.8 2.8 0 0 0-1.16-.384 2.93 2.93 0 0 0-2.132.6 3.5 3.5 0 0 0-.835.932l-.02.029c-.037-.053-.068-.103-.102-.151A3.3 3.3 0 0 0 5.145.557 2.8 2.8 0 0 0 3.85.25q-.197 0-.394.025-.525.066-.988.311");

    private static readonly Brush HeartFill = CreateFill();

    public ReactionMark()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size available) => new(MarkWidth, MarkHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var scale = Math.Min(ActualWidth / MarkWidth, ActualHeight / MarkHeight);
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(HeartFill, null, Heart);
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
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x57, 0x52));
        brush.Freeze();
        return brush;
    }
}
