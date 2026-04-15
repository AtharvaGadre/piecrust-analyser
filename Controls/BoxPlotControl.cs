using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Controls;

public sealed class BoxPlotControl : Control
{
    static BoxPlotControl()
    {
        AffectsRender<BoxPlotControl>(DatasetsProperty, XAxisTitleProperty);
    }

    public static readonly StyledProperty<IReadOnlyList<BoxPlotDataset>?> DatasetsProperty =
        AvaloniaProperty.Register<BoxPlotControl, IReadOnlyList<BoxPlotDataset>?>(nameof(Datasets));

    public static readonly StyledProperty<string?> XAxisTitleProperty =
        AvaloniaProperty.Register<BoxPlotControl, string?>(nameof(XAxisTitle), "Sequence");

    public IReadOnlyList<BoxPlotDataset>? Datasets
    {
        get => GetValue(DatasetsProperty);
        set => SetValue(DatasetsProperty, value);
    }

    public string? XAxisTitle
    {
        get => GetValue(XAxisTitleProperty);
        set => SetValue(XAxisTitleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#15100b")), null, rect);
        var datasets = Datasets?.Where(d => d.Stats.Count > 0).ToArray() ?? Array.Empty<BoxPlotDataset>();
        if (datasets.Length == 0) return;

        const double padL = 44;
        const double padR = 18;
        const double padT = 10;
        const double padB = 58;
        var plotRect = new Rect(padL, padT, Math.Max(1, Bounds.Width - padL - padR), Math.Max(1, Bounds.Height - padT - padB));
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#1b130b")), new Pen(new SolidColorBrush(Color.Parse("#3b2a18")), 1), plotRect);

        var (yMin, yMax, tickStep) = BuildNiceAxis(datasets);

        double ToY(double y) => plotRect.Bottom - (y - yMin) / (yMax - yMin) * plotRect.Height;
        var slot = plotRect.Width / Math.Max(1, datasets.Length);

        for (var i = 0; i < 5; i++)
        {
            var y = plotRect.Y + i * plotRect.Height / 4.0;
            var tickValue = yMin + tickStep * (4 - i);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#24170d")), 0.6), new Point(plotRect.X, y), new Point(plotRect.Right, y));
            DrawText(context, FormatAxisValue(tickValue), new Point(2, y - 7), "#8a7a66", 7);
        }

        for (var i = 0; i < datasets.Length; i++)
        {
            var d = datasets[i];
            var cx = plotRect.X + slot * (i + 0.5);
            var boxWidth = Math.Min(28, slot * 0.45);
            var color = new SolidColorBrush(Color.Parse(d.Color));
            var pen = new Pen(color, 1.4);
            var boxRect = new Rect(cx - boxWidth / 2, ToY(d.Stats.Q3), boxWidth, Math.Max(3, ToY(d.Stats.Q1) - ToY(d.Stats.Q3)));
            context.DrawRectangle(new SolidColorBrush(Color.Parse(d.Color), 0.25), pen, boxRect);
            context.DrawLine(pen, new Point(cx - boxWidth / 2, ToY(d.Stats.Median)), new Point(cx + boxWidth / 2, ToY(d.Stats.Median)));
            context.DrawLine(pen, new Point(cx, ToY(d.Stats.WhiskerHigh)), new Point(cx, ToY(d.Stats.Q3)));
            context.DrawLine(pen, new Point(cx, ToY(d.Stats.WhiskerLow)), new Point(cx, ToY(d.Stats.Q1)));
            context.DrawLine(pen, new Point(cx - boxWidth / 3, ToY(d.Stats.WhiskerHigh)), new Point(cx + boxWidth / 3, ToY(d.Stats.WhiskerHigh)));
            context.DrawLine(pen, new Point(cx - boxWidth / 3, ToY(d.Stats.WhiskerLow)), new Point(cx + boxWidth / 3, ToY(d.Stats.WhiskerLow)));
            var mean = double.IsFinite(d.MeanMarker) ? d.MeanMarker : d.Stats.Mean;
            var meanY = ToY(mean);
            var sem = Math.Max(0, d.MeanError > 0 ? d.MeanError : d.Stats.StandardError);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#f0c978")), 1.1), new Point(cx + boxWidth / 2 + 5, ToY(mean - sem)), new Point(cx + boxWidth / 2 + 5, ToY(mean + sem)));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#f0c978")), 1.1), new Point(cx + boxWidth / 2 + 2, ToY(mean - sem)), new Point(cx + boxWidth / 2 + 8, ToY(mean - sem)));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#f0c978")), 1.1), new Point(cx + boxWidth / 2 + 2, ToY(mean + sem)), new Point(cx + boxWidth / 2 + 8, ToY(mean + sem)));
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#fff3d1")), null, new Point(cx + boxWidth / 2 + 5, meanY), 2, 2);
            DrawCenteredText(context, TrimLabel(d.Label), new Point(cx, plotRect.Bottom + 8), "#8a7a66", 8);
        }

        if (!string.IsNullOrWhiteSpace(XAxisTitle))
        {
            DrawCenteredText(context, XAxisTitle!, new Point(plotRect.Center.X, Bounds.Height - 18), "#a18d72", 9);
        }
    }

    private static string TrimLabel(string label) => label.Length <= 12 ? label : label[..11] + "…";

    private static (double Min, double Max, double TickStep) BuildNiceAxis(IReadOnlyList<BoxPlotDataset> datasets)
    {
        var lower = datasets.Min(dataset =>
        {
            var mean = double.IsFinite(dataset.MeanMarker) ? dataset.MeanMarker : dataset.Stats.Mean;
            var sem = Math.Max(0, dataset.MeanError > 0 ? dataset.MeanError : dataset.Stats.StandardError);
            return Math.Min(dataset.Stats.WhiskerLow, mean - sem);
        });
        var upper = datasets.Max(dataset =>
        {
            var mean = double.IsFinite(dataset.MeanMarker) ? dataset.MeanMarker : dataset.Stats.Mean;
            var sem = Math.Max(0, dataset.MeanError > 0 ? dataset.MeanError : dataset.Stats.StandardError);
            return Math.Max(dataset.Stats.WhiskerHigh, mean + sem);
        });

        if (!(upper > lower))
        {
            upper = lower + 1;
        }

        var span = upper - lower;
        var paddedLower = lower - span * 0.08;
        var paddedUpper = upper + span * 0.08;
        var rawStep = Math.Max(1e-6, (paddedUpper - paddedLower) / 4.0);
        var tickStep = NiceStep(rawStep);
        var min = paddedLower >= 0
            ? 0
            : Math.Floor(paddedLower / tickStep) * tickStep;
        var max = Math.Ceiling(paddedUpper / tickStep) * tickStep;
        if (!(max > min))
        {
            max = min + tickStep * 4;
        }

        return (min, max, (max - min) / 4.0);
    }

    private static double NiceStep(double value)
    {
        var exponent = Math.Floor(Math.Log10(Math.Max(1e-9, value)));
        var fraction = value / Math.Pow(10, exponent);
        var niceFraction = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        return niceFraction * Math.Pow(10, exponent);
    }

    private static string FormatAxisValue(double value)
    {
        var abs = Math.Abs(value);
        if (abs >= 100) return value.ToString("F0", CultureInfo.InvariantCulture);
        if (abs >= 10) return value.ToString("F1", CultureInfo.InvariantCulture);
        if (abs >= 1) return value.ToString("F2", CultureInfo.InvariantCulture);
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static void DrawText(DrawingContext context, string text, Point point, string colorHex, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            size,
            new SolidColorBrush(Color.Parse(colorHex)));
        context.DrawText(formatted, point);
    }

    private static void DrawCenteredText(DrawingContext context, string text, Point center, string colorHex, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            size,
            new SolidColorBrush(Color.Parse(colorHex)));
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y));
    }
}
