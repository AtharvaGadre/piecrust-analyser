using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Controls;

public sealed class LinePlotControl : Control
{
    static LinePlotControl()
    {
        AffectsRender<LinePlotControl>(
            SeriesProperty,
            TitleProperty,
            XAxisLabelProperty,
            YAxisLabelProperty,
            LightThemeProperty,
            FixedXMinProperty,
            FixedXMaxProperty,
            FixedYMinProperty,
            FixedYMaxProperty);
    }

    public static readonly StyledProperty<IReadOnlyList<PolylineSeries>?> SeriesProperty =
        AvaloniaProperty.Register<LinePlotControl, IReadOnlyList<PolylineSeries>?>(nameof(Series));
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<LinePlotControl, string>(nameof(Title), string.Empty);
    public static readonly StyledProperty<string> XAxisLabelProperty =
        AvaloniaProperty.Register<LinePlotControl, string>(nameof(XAxisLabel), string.Empty);
    public static readonly StyledProperty<string> YAxisLabelProperty =
        AvaloniaProperty.Register<LinePlotControl, string>(nameof(YAxisLabel), string.Empty);
    public static readonly StyledProperty<bool> LightThemeProperty =
        AvaloniaProperty.Register<LinePlotControl, bool>(nameof(LightTheme));
    public static readonly StyledProperty<double> FixedXMinProperty =
        AvaloniaProperty.Register<LinePlotControl, double>(nameof(FixedXMin), double.NaN);
    public static readonly StyledProperty<double> FixedXMaxProperty =
        AvaloniaProperty.Register<LinePlotControl, double>(nameof(FixedXMax), double.NaN);
    public static readonly StyledProperty<double> FixedYMinProperty =
        AvaloniaProperty.Register<LinePlotControl, double>(nameof(FixedYMin), double.NaN);
    public static readonly StyledProperty<double> FixedYMaxProperty =
        AvaloniaProperty.Register<LinePlotControl, double>(nameof(FixedYMax), double.NaN);

    public IReadOnlyList<PolylineSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string XAxisLabel
    {
        get => GetValue(XAxisLabelProperty);
        set => SetValue(XAxisLabelProperty, value);
    }

    public string YAxisLabel
    {
        get => GetValue(YAxisLabelProperty);
        set => SetValue(YAxisLabelProperty, value);
    }

    public bool LightTheme
    {
        get => GetValue(LightThemeProperty);
        set => SetValue(LightThemeProperty, value);
    }

    public double FixedXMin
    {
        get => GetValue(FixedXMinProperty);
        set => SetValue(FixedXMinProperty, value);
    }

    public double FixedXMax
    {
        get => GetValue(FixedXMaxProperty);
        set => SetValue(FixedXMaxProperty, value);
    }

    public double FixedYMin
    {
        get => GetValue(FixedYMinProperty);
        set => SetValue(FixedYMinProperty, value);
    }

    public double FixedYMax
    {
        get => GetValue(FixedYMaxProperty);
        set => SetValue(FixedYMaxProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var theme = LightTheme
            ? new PlotTheme("#f4f0e7", "#fffdf7", "#d8d2c5", "#555555", "#333333", "#e4ddd1")
            : new PlotTheme("#15100b", "#1c140d", "#3b2a18", "#9f8866", "#d8b07a", "#24170d");
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(new SolidColorBrush(Color.Parse(theme.Surface)), null, rect);

        const double padL = 88;
        const double padR = 28;
        const double padT = 38;
        const double padB = 66;
        var plotRect = new Rect(padL, padT, Math.Max(1, Bounds.Width - padL - padR), Math.Max(1, Bounds.Height - padT - padB));
        context.DrawRectangle(new SolidColorBrush(Color.Parse(theme.PlotBackground)), new Pen(new SolidColorBrush(Color.Parse(theme.Border)), 1), plotRect);
        DrawCenteredText(context, Title, new Point(plotRect.Center.X, 14), theme.Title, 10, FontWeight.SemiBold);

        var series = Series?.Where(s => s.Points.Count > 1).ToArray() ?? Array.Empty<PolylineSeries>();
        if (series.Length == 0)
        {
            DrawAxisLabels(context, plotRect, theme);
            return;
        }

        var allPoints = series.SelectMany(s => s.Points).ToArray();
        var minX = double.IsFinite(FixedXMin) ? FixedXMin : allPoints.Min(p => p.X);
        var maxX = double.IsFinite(FixedXMax) ? FixedXMax : allPoints.Max(p => p.X);
        var minY = double.IsFinite(FixedYMin) ? FixedYMin : allPoints.Min(p => p.Y);
        var maxY = double.IsFinite(FixedYMax) ? FixedYMax : allPoints.Max(p => p.Y);
        if (!(maxX > minX)) maxX = minX + 1;
        if (!(maxY > minY)) maxY = minY + 1;

        for (var i = 0; i < 5; i++)
        {
            var y = plotRect.Y + i * plotRect.Height / 4.0;
            var tickValue = maxY - i * (maxY - minY) / 4.0;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(theme.Grid)), 0.6), new Point(plotRect.X, y), new Point(plotRect.Right, y));
            DrawRightAlignedText(context, FormatAxisValue(tickValue), new Point(plotRect.X - 10, y - 7), theme.Axis, 8);
        }

        for (var i = 0; i < 5; i++)
        {
            var x = plotRect.X + i * plotRect.Width / 4.0;
            var tickValue = minX + i * (maxX - minX) / 4.0;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(theme.Grid)), 0.6), new Point(x, plotRect.Y), new Point(x, plotRect.Bottom));
            DrawCenteredText(context, FormatAxisValue(tickValue), new Point(x, plotRect.Bottom + 9), theme.Axis, 8);
        }

        foreach (var line in series)
        {
            var points = new List<Point>(line.Points.Count);
            foreach (var pt in line.Points)
            {
                var x = plotRect.X + (pt.X - minX) / (maxX - minX) * plotRect.Width;
                var y = plotRect.Bottom - (pt.Y - minY) / (maxY - minY) * plotRect.Height;
                points.Add(new Point(x, y));
            }

            var brush = new SolidColorBrush(Color.Parse(line.Color), line.Opacity);
            var pen = line.Dotted
                ? new Pen(brush, line.Thickness, dashStyle: new DashStyle(new[] { 1.2d, 4.2d }, 0))
                : line.Dashed
                    ? new Pen(brush, line.Thickness, dashStyle: new DashStyle(new[] { 5d, 4d }, 0))
                    : new Pen(brush, line.Thickness);
            for (var i = 1; i < points.Count; i++) context.DrawLine(pen, points[i - 1], points[i]);
        }

        DrawAxisLabels(context, plotRect, theme);
    }

    private void DrawAxisLabels(DrawingContext context, Rect plotRect, PlotTheme theme)
    {
        DrawCenteredText(context, XAxisLabel, new Point(plotRect.Center.X, Bounds.Height - 30), theme.Axis, 9);
        var yText = CreateFormattedText(YAxisLabel, theme.Axis, 9, null);
        using var rotation = context.PushTransform(Matrix.CreateTranslation(26, plotRect.Center.Y) * Matrix.CreateRotation(-Math.PI / 2));
        context.DrawText(yText, new Point(-yText.Width / 2, -yText.Height / 2));
    }

    private static string FormatAxisValue(double value)
    {
        var abs = Math.Abs(value);
        if (abs >= 100) return value.ToString("F0", CultureInfo.InvariantCulture);
        if (abs >= 10) return value.ToString("F1", CultureInfo.InvariantCulture);
        if (abs >= 1) return value.ToString("F2", CultureInfo.InvariantCulture);
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static void DrawText(DrawingContext context, string? text, Point point, string colorHex, double size, FontWeight? fontWeight = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var formatted = CreateFormattedText(text, colorHex, size, fontWeight);
        context.DrawText(formatted, point);
    }

    private static void DrawCenteredText(DrawingContext context, string? text, Point center, string colorHex, double size, FontWeight? fontWeight = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var formatted = CreateFormattedText(text, colorHex, size, fontWeight);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y));
    }

    private static void DrawRightAlignedText(DrawingContext context, string? text, Point rightAnchor, string colorHex, double size, FontWeight? fontWeight = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var formatted = CreateFormattedText(text, colorHex, size, fontWeight);
        context.DrawText(formatted, new Point(rightAnchor.X - formatted.Width, rightAnchor.Y));
    }

    private static FormattedText CreateFormattedText(string? text, string colorHex, double size, FontWeight? fontWeight)
    {
        return new FormattedText(
            text ?? string.Empty,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(fontFamily: FontFamily.Default, weight: fontWeight ?? FontWeight.Normal),
            size,
            new SolidColorBrush(Color.Parse(colorHex)));
    }

    private readonly record struct PlotTheme(string Surface, string PlotBackground, string Border, string Axis, string Title, string Grid);
}
