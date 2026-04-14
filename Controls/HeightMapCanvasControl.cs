using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Controls;

public sealed class PixelSelectedEventArgs : EventArgs
{
    public PixelSelectedEventArgs(PointD point) => Point = point;
    public PointD Point { get; }
}

public sealed class HeightMapCanvasControl : Control
{
    public HeightMapCanvasControl()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    static HeightMapCanvasControl()
    {
        AffectsRender<HeightMapCanvasControl>(
            BitmapProperty,
            ProfileLineProperty,
            GuidePointsProperty,
            GuideFinishedProperty,
            CorridorHalfWidthPixelsProperty,
            CorridorWidthLabelProperty);
    }

    public static readonly StyledProperty<WriteableBitmap?> BitmapProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, WriteableBitmap?>(nameof(Bitmap));

    public static readonly StyledProperty<IReadOnlyList<PointD>?> ProfileLineProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, IReadOnlyList<PointD>?>(nameof(ProfileLine));

    public static readonly StyledProperty<IReadOnlyList<PointD>?> GuidePointsProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, IReadOnlyList<PointD>?>(nameof(GuidePoints));

    public static readonly StyledProperty<bool> GuideFinishedProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, bool>(nameof(GuideFinished));

    public static readonly StyledProperty<double> CorridorHalfWidthPixelsProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, double>(nameof(CorridorHalfWidthPixels), 10);

    public static readonly StyledProperty<string?> CorridorWidthLabelProperty =
        AvaloniaProperty.Register<HeightMapCanvasControl, string?>(nameof(CorridorWidthLabel));

    public WriteableBitmap? Bitmap
    {
        get => GetValue(BitmapProperty);
        set => SetValue(BitmapProperty, value);
    }

    public IReadOnlyList<PointD>? ProfileLine
    {
        get => GetValue(ProfileLineProperty);
        set => SetValue(ProfileLineProperty, value);
    }

    public IReadOnlyList<PointD>? GuidePoints
    {
        get => GetValue(GuidePointsProperty);
        set => SetValue(GuidePointsProperty, value);
    }

    public bool GuideFinished
    {
        get => GetValue(GuideFinishedProperty);
        set => SetValue(GuideFinishedProperty, value);
    }

    public double CorridorHalfWidthPixels
    {
        get => GetValue(CorridorHalfWidthPixelsProperty);
        set => SetValue(CorridorHalfWidthPixelsProperty, value);
    }

    public string? CorridorWidthLabel
    {
        get => GetValue(CorridorWidthLabelProperty);
        set => SetValue(CorridorWidthLabelProperty, value);
    }

    public event EventHandler<PixelSelectedEventArgs>? PixelSelected;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bitmap is null) return;
        if (!TryMapToPixel(e.GetPosition(this), out var point)) return;
        PixelSelected?.Invoke(this, new PixelSelectedEventArgs(point));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#120d08")), null, rect);
        if (Bitmap is null) return;

        var imageRect = GetImageRect();
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
               }))
        {
            context.DrawImage(Bitmap, new Rect(0, 0, Bitmap.PixelSize.Width, Bitmap.PixelSize.Height), imageRect);
        }
        DrawOriginMarker(context, imageRect);
        DrawGuide(context, imageRect);
        DrawProfile(context, imageRect);
    }

    private void DrawOriginMarker(DrawingContext context, Rect imageRect)
    {
        var p = new Point(imageRect.X + 8, imageRect.Y + 8);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#00000066")), null, new Rect(p, new Size(42, 18)));
        var pen = new Pen(new SolidColorBrush(Colors.White), 1);
        context.DrawLine(pen, new Point(p.X + 6, p.Y + 6), new Point(p.X + 18, p.Y + 6));
        context.DrawLine(pen, new Point(p.X + 6, p.Y + 6), new Point(p.X + 6, p.Y + 18));
    }

    private void DrawProfile(DrawingContext context, Rect imageRect)
    {
        if (ProfileLine is null || ProfileLine.Count == 0) return;
        var haloPen = new Pen(new SolidColorBrush(Color.Parse("#000000cc")), 4.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#fff8de")), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (var i = 1; i < ProfileLine.Count; i++)
        {
            var p0 = ToCanvas(ProfileLine[i - 1], imageRect);
            var p1 = ToCanvas(ProfileLine[i], imageRect);
            context.DrawLine(haloPen, p0, p1);
            context.DrawLine(pen, p0, p1);
        }

        for (var i = 0; i < ProfileLine.Count; i++)
        {
            var canvas = ToCanvas(ProfileLine[i], imageRect);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#111111ee")), null, canvas, 6, 6);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#ffd78b")), null, canvas, 4, 4);
        }
    }

    private void DrawGuide(DrawingContext context, Rect imageRect)
    {
        if (GuidePoints is null || GuidePoints.Count == 0) return;
        var bandThickness = Math.Max(4, CorridorHalfWidthPixels * imageRect.Width / Math.Max(1, Bitmap?.PixelSize.Width ?? 1) * 2);
        var widePen = new Pen(new SolidColorBrush(Color.Parse("#72d8ff33")), bandThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var guideHaloPen = new Pen(new SolidColorBrush(Color.Parse("#031018cc")), 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var guidePen = new Pen(new SolidColorBrush(Color.Parse("#7ed9ff")), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var boundaryPen = new Pen(new SolidColorBrush(Color.Parse("#f0c978")), 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (GuideFinished && GuidePoints.Count >= 2)
        {
            for (var i = 1; i < GuidePoints.Count; i++)
            {
                var p0 = GuidePoints[i - 1];
                var p1 = GuidePoints[i];
                var c0 = ToCanvas(p0, imageRect);
                var c1 = ToCanvas(p1, imageRect);
                context.DrawLine(widePen, c0, c1);
                foreach (var boundary in GetCorridorBoundaryPoints(p0, p1, imageRect))
                {
                    context.DrawLine(boundaryPen, boundary.Start, boundary.End);
                }
            }
        }
        for (var i = 1; i < GuidePoints.Count; i++)
        {
            var c0 = ToCanvas(GuidePoints[i - 1], imageRect);
            var c1 = ToCanvas(GuidePoints[i], imageRect);
            context.DrawLine(guideHaloPen, c0, c1);
            context.DrawLine(guidePen, c0, c1);
        }
        for (var i = 0; i < GuidePoints.Count; i++)
        {
            var canvas = ToCanvas(GuidePoints[i], imageRect);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#06131acc")), null, canvas, 6.5, 6.5);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#fff4d8")), null, canvas, 4, 4);
        }
    }

    private Rect GetImageRect()
    {
        if (Bitmap is null) return new Rect(0, 0, 0, 0);
        var bounds = Bounds;
        var imgWidth = Bitmap.PixelSize.Width;
        var imgHeight = Bitmap.PixelSize.Height;
        var scale = Math.Min(bounds.Width / Math.Max(1, imgWidth), bounds.Height / Math.Max(1, imgHeight));
        var drawWidth = imgWidth * scale;
        var drawHeight = imgHeight * scale;
        var x = (bounds.Width - drawWidth) / 2;
        var y = (bounds.Height - drawHeight) / 2;
        return new Rect(x, y, drawWidth, drawHeight);
    }

    private Point ToCanvas(PointD point, Rect imageRect)
    {
        if (Bitmap is null) return default;
        var x = imageRect.X + point.X / Math.Max(1, Bitmap.PixelSize.Width) * imageRect.Width;
        var y = imageRect.Y + point.Y / Math.Max(1, Bitmap.PixelSize.Height) * imageRect.Height;
        return new Point(x, y);
    }

    private IEnumerable<(Point Start, Point End)> GetCorridorBoundaryPoints(PointD a, PointD b, Rect imageRect)
    {
        if (Bitmap is null) yield break;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) yield break;

        var nx = -dy / len;
        var ny = dx / len;
        var offset = CorridorHalfWidthPixels;

        var left0 = ToCanvas(new PointD(a.X + nx * offset, a.Y + ny * offset), imageRect);
        var left1 = ToCanvas(new PointD(b.X + nx * offset, b.Y + ny * offset), imageRect);
        var right0 = ToCanvas(new PointD(a.X - nx * offset, a.Y - ny * offset), imageRect);
        var right1 = ToCanvas(new PointD(b.X - nx * offset, b.Y - ny * offset), imageRect);
        yield return (left0, left1);
        yield return (right0, right1);
    }

    private bool TryMapToPixel(Point canvasPoint, out PointD imagePoint)
    {
        imagePoint = default;
        if (Bitmap is null) return false;
        var imageRect = GetImageRect();
        if (!imageRect.Contains(canvasPoint)) return false;
        var x = (canvasPoint.X - imageRect.X) / Math.Max(1e-9, imageRect.Width) * Bitmap.PixelSize.Width;
        var y = (canvasPoint.Y - imageRect.Y) / Math.Max(1e-9, imageRect.Height) * Bitmap.PixelSize.Height;
        imagePoint = new PointD(Math.Clamp(x, 0, Bitmap.PixelSize.Width - 1), Math.Clamp(y, 0, Bitmap.PixelSize.Height - 1));
        return true;
    }
}
