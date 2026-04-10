using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class PreviewBitmapService
{
    public (WriteableBitmap Bitmap, double Min, double Max) Render(double[] data, int width, int height, double? min = null, double? max = null, bool scientificPreview = false)
    {
        try
        {
            if (data.Length == 0 || width <= 0 || height <= 0)
            {
                return (CreateFallbackBitmap(), 0, 1);
            }

            var safeCount = Math.Min(data.Length, Math.Max(1, width * height));
            if (safeCount <= 0) return (CreateFallbackBitmap(), 0, 1);

            var displayData = scientificPreview ? BuildScientificPreviewData(data, width, height, safeCount) : BuildPlainPreviewData(data, safeCount);
            var range = GetRobustRange(displayData);
            var lo = min ?? range.Min;
            var hi = max ?? range.Max;
            if (!(hi > lo))
            {
                lo = range.Min;
                hi = range.Max > range.Min ? range.Max : range.Min + 1;
            }

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = bitmap.Lock();
            unsafe
            {
                var ptr = (byte*)fb.Address;
                for (var y = 0; y < height; y++)
                {
                    var row = ptr + y * fb.RowBytes;
                    for (var x = 0; x < width; x++)
                    {
                        var index = y * width + x;
                        var value = index < safeCount && double.IsFinite(displayData[index]) ? displayData[index] : lo;
                        var t = Math.Clamp((value - lo) / Math.Max(1e-9, hi - lo), 0, 1);
                        var color = GwyddionColor(Math.Pow(t, 0.92));
                        var offset = x * 4;
                        row[offset + 0] = color.B;
                        row[offset + 1] = color.G;
                        row[offset + 2] = color.R;
                        row[offset + 3] = 255;
                    }
                }
            }

            return (bitmap, lo, hi);
        }
        catch
        {
            return (CreateFallbackBitmap(), 0, 1);
        }
    }

    private static double[] BuildPlainPreviewData(double[] data, int count)
    {
        var output = new double[count];
        for (var i = 0; i < count; i++)
        {
            var value = data[i];
            output[i] = double.IsFinite(value) ? value : 0;
        }
        return output;
    }

    private static double[] BuildScientificPreviewData(double[] data, int width, int height, int count)
    {
        var output = new double[count];
        Array.Copy(data, output, count);

        for (var y = 0; y < height; y++)
        {
            var start = y * width;
            if (start >= count) break;
            var length = Math.Min(width, count - start);
            if (length <= 0) break;
            var row = new double[length];
            for (var i = 0; i < length; i++)
            {
                var value = output[start + i];
                row[i] = double.IsFinite(value) ? value : 0;
            }
            Array.Sort(row);
            var median = row[length / 2];
            for (var i = 0; i < length; i++)
            {
                var value = output[start + i];
                output[start + i] = (double.IsFinite(value) ? value : median) - median;
            }
        }

        var globalMedian = EstimateQuantile(output, 0.5);
        for (var i = 0; i < output.Length; i++) output[i] -= globalMedian;
        return output;
    }

    private static (double Min, double Max) GetRobustRange(double[] data)
    {
        if (data.Length == 0) return (0, 1);
        var lo = EstimateQuantile(data, 0.02);
        var hi = EstimateQuantile(data, 0.98);
        if (!(hi > lo))
        {
            lo = EstimateQuantile(data, 0.01);
            hi = EstimateQuantile(data, 0.99);
        }
        if (!(hi > lo))
        {
            lo = data.Min();
            hi = data.Max();
        }
        if (!(hi > lo)) return (0, 1);
        return (lo, hi);
    }

    private static double EstimateQuantile(double[] data, double q)
    {
        if (data.Length == 0) return 0;
        var step = Math.Max(1, data.Length / 8192);
        var sample = new List<double>(Math.Min(data.Length, 8192));
        for (var i = 0; i < data.Length; i += step)
        {
            var value = data[i];
            if (double.IsFinite(value)) sample.Add(value);
        }
        if (sample.Count == 0) return 0;
        sample.Sort();
        var index = (int)Math.Clamp(Math.Round((sample.Count - 1) * q), 0, sample.Count - 1);
        return sample[index];
    }

    private static WriteableBitmap CreateFallbackBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        unsafe
        {
            var ptr = (byte*)fb.Address;
            for (var y = 0; y < 2; y++)
            {
                var row = ptr + y * fb.RowBytes;
                for (var x = 0; x < 2; x++)
                {
                    var offset = x * 4;
                    row[offset + 0] = 28;
                    row[offset + 1] = 22;
                    row[offset + 2] = 18;
                    row[offset + 3] = 255;
                }
            }
        }
        return bitmap;
    }

    private static Color GwyddionColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var stops = new[]
        {
            (0.00, Color.Parse("#000000")),
            (0.04, Color.Parse("#0f0502")),
            (0.10, Color.Parse("#280e04")),
            (0.18, Color.Parse("#481a06")),
            (0.26, Color.Parse("#6e2a08")),
            (0.35, Color.Parse("#943e0a")),
            (0.44, Color.Parse("#b4580e")),
            (0.53, Color.Parse("#cd7612")),
            (0.62, Color.Parse("#e09a1c")),
            (0.72, Color.Parse("#eec034")),
            (0.82, Color.Parse("#f8e064")),
            (0.90, Color.Parse("#fef4a6")),
            (0.96, Color.Parse("#fffcdc")),
            (1.00, Color.Parse("#ffffff"))
        };

        for (var i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].Item1) continue;
            var (t0, c0) = stops[i - 1];
            var (t1, c1) = stops[i];
            var mix = (t - t0) / Math.Max(1e-9, t1 - t0);
            byte Lerp(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * mix), 0, 255);
            return Color.FromRgb(Lerp(c0.R, c1.R), Lerp(c0.G, c1.G), Lerp(c0.B, c1.B));
        }

        return stops[^1].Item2;
    }
}
