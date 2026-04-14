using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PiecrustAnalyser.CSharp.Controls;

public sealed class DisplayRangeHistogramControl : Control
{
    static DisplayRangeHistogramControl()
    {
        AffectsRender<DisplayRangeHistogramControl>(HistogramProperty, WindowStartPercentProperty, WindowEndPercentProperty);
    }

    public static readonly StyledProperty<IReadOnlyList<double>?> HistogramProperty =
        AvaloniaProperty.Register<DisplayRangeHistogramControl, IReadOnlyList<double>?>(nameof(Histogram));

    public static readonly StyledProperty<double> WindowStartPercentProperty =
        AvaloniaProperty.Register<DisplayRangeHistogramControl, double>(nameof(WindowStartPercent));

    public static readonly StyledProperty<double> WindowEndPercentProperty =
        AvaloniaProperty.Register<DisplayRangeHistogramControl, double>(nameof(WindowEndPercent), 100);

    public IReadOnlyList<double>? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public double WindowStartPercent
    {
        get => GetValue(WindowStartPercentProperty);
        set => SetValue(WindowStartPercentProperty, value);
    }

    public double WindowEndPercent
    {
        get => GetValue(WindowEndPercentProperty);
        set => SetValue(WindowEndPercentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        context.DrawRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse("#f3ece1"), 0),
                    new GradientStop(Color.Parse("#d6d0ca"), 1)
                ]
            },
            new Pen(new SolidColorBrush(Color.Parse("#3a2a16")), 1),
            bounds);

        var inner = bounds.Deflate(4);
        if (inner.Width <= 1 || inner.Height <= 1) return;

        var start = Math.Clamp(WindowStartPercent, 0, 100);
        var end = Math.Clamp(WindowEndPercent, start, 100);
        var selectionRect = new Rect(
            inner.X + inner.Width * start / 100.0,
            inner.Y,
            inner.Width * Math.Max(0, end - start) / 100.0,
            inner.Height);

        context.DrawRectangle(
            new SolidColorBrush(Color.Parse("#c46cb0"), 0.45),
            null,
            selectionRect);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#ffe6ff")), 2), selectionRect.TopLeft, selectionRect.BottomLeft);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#ffe6ff")), 2), selectionRect.TopRight, selectionRect.BottomRight);

        var histogram = Histogram?.ToArray() ?? Array.Empty<double>();
        if (histogram.Length == 0) return;

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(inner.X, inner.Bottom), true);
            for (var i = 0; i < histogram.Length; i++)
            {
                var x = inner.X + inner.Width * i / Math.Max(1, histogram.Length - 1);
                var y = inner.Y + (1 - Math.Clamp(histogram[i], 0, 1)) * inner.Height;
                stream.LineTo(new Point(x, y));
            }
            stream.LineTo(new Point(inner.Right, inner.Bottom));
            stream.EndFigure(true);
        }

        context.DrawGeometry(
            new SolidColorBrush(Color.Parse("#481c12"), 0.18),
            new Pen(new SolidColorBrush(Color.Parse("#160d09")), 1.2),
            geometry);
    }
}
