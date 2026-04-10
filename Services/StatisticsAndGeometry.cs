using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public static class StatisticsAndGeometry
{
    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    public static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    public static double StandardDeviation(IReadOnlyList<double> values, double? mean = null)
    {
        if (values.Count == 0) return 0;
        var mu = mean ?? Mean(values);
        double sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var d = values[i] - mu;
            sum += d * d;
        }
        return Math.Sqrt(sum / Math.Max(1, values.Count));
    }

    public static double StandardError(IReadOnlyList<double> values) => values.Count == 0 ? 0 : StandardDeviation(values) / Math.Sqrt(values.Count);

    public static DistributionSummary? Summarise(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var mean = Mean(sorted);
        var sd = StandardDeviation(sorted, mean);
        var se = sd / Math.Sqrt(sorted.Length);
        double Q(double q) => sorted[(int)Math.Clamp(Math.Round((sorted.Length - 1) * q), 0, sorted.Length - 1)];
        var q1 = Q(0.25);
        var median = Q(0.5);
        var q3 = Q(0.75);
        var iqr = q3 - q1;
        var whiskerLow = Math.Max(sorted[0], q1 - 1.5 * iqr);
        var whiskerHigh = Math.Min(sorted[^1], q3 + 1.5 * iqr);
        return new DistributionSummary
        {
            Min = sorted[0],
            Q1 = q1,
            Median = median,
            Q3 = q3,
            Max = sorted[^1],
            WhiskerLow = whiskerLow,
            WhiskerHigh = whiskerHigh,
            Mean = mean,
            StandardDeviation = sd,
            StandardError = se,
            Count = sorted.Length,
            Outliers = sorted.Where(v => v < whiskerLow || v > whiskerHigh).ToArray()
        };
    }

    public static double PolylineLengthPixels(IReadOnlyList<PointD> points)
    {
        if (points.Count < 2) return 0;
        double sum = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum;
    }

    public static List<(PointD Point, double ArcNm)> SampleCurveAtPhysicalInterval(IReadOnlyList<PointD> points, double intervalNm, double nmPerPixel)
    {
        var result = new List<(PointD Point, double ArcNm)>();
        if (points.Count < 2) return result;
        var stepPx = Math.Max(0.25, intervalNm / Math.Max(1e-9, nmPerPixel));
        double arcPx = 0;
        result.Add((points[0], 0));
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (!(len > 0)) continue;
            var count = Math.Max(1, (int)Math.Round(len / stepPx));
            for (var k = 1; k <= count; k++)
            {
                var t = k / (double)count;
                var p = new PointD(a.X + dx * t, a.Y + dy * t);
                var newArcPx = arcPx + len * t;
                if (result.Count == 0 || Distance(result[^1].Point, p) >= stepPx * 0.6)
                {
                    result.Add((p, newArcPx * nmPerPixel));
                }
            }
            arcPx += len;
        }
        return result;
    }

    public static PointD GetCurveTangent(IReadOnlyList<(PointD Point, double ArcNm)> sampled, int index)
    {
        if (sampled.Count == 0) return new PointD(1, 0);
        var lo = Math.Max(0, index - 1);
        var hi = Math.Min(sampled.Count - 1, index + 1);
        var dx = sampled[hi].Point.X - sampled[lo].Point.X;
        var dy = sampled[hi].Point.Y - sampled[lo].Point.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len > 0 ? new PointD(dx / len, dy / len) : new PointD(1, 0);
    }

    public static double BilinearClamped(double[] data, int width, int height, double x, double y)
    {
        if (width <= 0 || height <= 0 || data.Length == 0) return 0;
        x = Clamp(x, 0, width - 1);
        y = Clamp(y, 0, height - 1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        var i00 = data[y0 * width + x0];
        var i10 = data[y0 * width + x1];
        var i01 = data[y1 * width + x0];
        var i11 = data[y1 * width + x1];
        var a = i00 * (1 - fx) + i10 * fx;
        var b = i01 * (1 - fx) + i11 * fx;
        return a * (1 - fy) + b * fy;
    }

    public static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double[] MovingGaussianSmooth(IReadOnlyList<double> values, int radius = 2)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var output = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            double sum = 0;
            double weightSum = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var j = i + k;
                if (j < 0 || j >= values.Count) continue;
                var weight = Math.Exp(-(k * k) / (2.0 * Math.Max(1, radius * radius)));
                sum += values[j] * weight;
                weightSum += weight;
            }
            output[i] = weightSum > 0 ? sum / weightSum : values[i];
        }
        return output;
    }
}
