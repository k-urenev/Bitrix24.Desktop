using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bitrix24.Desktop;

internal static class SmoothScroll
{
    private static readonly Dictionary<ScrollViewer, double> Targets = new();
    private static readonly HashSet<ScrollViewer> ChaseEnd = new();
    private static readonly HashSet<ScrollViewer> Hooked = new();
    private static bool _ticking;
    private static int _driving;

    public static bool Driving => _driving > 0;

    public static void Attach(ScrollViewer viewer)
    {
        if (!Hooked.Add(viewer))
            return;

        viewer.PreviewMouseWheel += (_, e) => Wheel(viewer, e);
        viewer.PreviewMouseDown += (_, _) => Cancel(viewer);
    }

    public static bool ChasingEnd(ScrollViewer viewer) => ChaseEnd.Contains(viewer);

    public static bool HasTarget(ScrollViewer viewer) => Targets.ContainsKey(viewer);

    public static void Cancel(ScrollViewer viewer)
    {
        ChaseEnd.Remove(viewer);
        Targets.Remove(viewer);
    }

    public static void Attach(ListBox list)
    {
        if (list.IsLoaded)
            HookList(list);
        else
            list.Loaded += (_, _) => HookList(list);
    }

    private static void HookList(ListBox list)
    {
        list.ApplyTemplate();
        if (FindViewer(list) is not ScrollViewer viewer)
            return;

        Attach(viewer);
    }

    private static void Wheel(ScrollViewer viewer, MouseWheelEventArgs e)
    {
        if (viewer.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0)
            return;

        e.Handled = true;
        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0
            ? viewer.ViewportHeight * 0.85
            : 100 * (lines / 3.0);
        distance *= Math.Abs(e.Delta) / 120.0 * 1.5;
        if (!Targets.TryGetValue(viewer, out var target))
            target = viewer.VerticalOffset;

        target = Math.Clamp(target - Math.Sign(e.Delta) * distance, 0, viewer.ScrollableHeight);
        ChaseEnd.Remove(viewer);
        Targets[viewer] = target;
        StartTicking();
    }

    public static void GlideToEnd(ScrollViewer viewer)
    {
        viewer.UpdateLayout();
        if (viewer.ScrollableHeight <= viewer.VerticalOffset + 0.6)
            return;

        ChaseEnd.Add(viewer);
        Targets[viewer] = viewer.ScrollableHeight;
        StartTicking();
    }

    private static void StartTicking()
    {
        if (_ticking)
            return;

        _ticking = true;
        CompositionTarget.Rendering += OnRender;
    }

    private static void OnRender(object? sender, EventArgs e)
    {
        foreach (var viewer in Targets.Keys.ToArray())
        {
            if (!Targets.TryGetValue(viewer, out var target))
                continue;

            if (ChaseEnd.Contains(viewer))
                target = viewer.ScrollableHeight;

            var delta = target - viewer.VerticalOffset;
            if (Math.Abs(delta) < 0.6)
            {
                Move(viewer, target);
                if (ChaseEnd.Contains(viewer))
                {
                    viewer.UpdateLayout();
                    if (viewer.ScrollableHeight > viewer.VerticalOffset + 0.6)
                    {
                        Targets[viewer] = viewer.ScrollableHeight;
                        continue;
                    }

                    ChaseEnd.Remove(viewer);
                }

                Targets.Remove(viewer);
                continue;
            }

            Targets[viewer] = target;
            Move(viewer, viewer.VerticalOffset + delta * 0.35);
        }

        if (Targets.Count > 0)
            return;

        CompositionTarget.Rendering -= OnRender;
        _ticking = false;
    }

    private static void Move(ScrollViewer viewer, double offset)
    {
        _driving++;
        try
        {
            viewer.ScrollToVerticalOffset(offset);
        }
        finally
        {
            _driving--;
        }
    }

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;

            var nested = FindViewer(child);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
