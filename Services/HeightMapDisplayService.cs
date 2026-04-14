using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class HeightMapDisplayService
{
    public LoadedHeightMap Prepare(LoadedHeightMap source)
    {
        var raw = source.Data?.Length > 0 ? source.Data.ToArray() : Array.Empty<double>();
        if (raw.Length == 0 || source.Width <= 0 || source.Height <= 0)
        {
            return source;
        }

        var calibratedSpm = IsCalibratedSpmDisplay(source);
        var analysis = raw.ToArray();
        var display = raw.ToArray();
        var displayReference = EstimateQuantile(raw, 0.5);
        var estimatedNoiseSigma = 0.0;

        if (source.PreferScientificPreview)
        {
            var leveled = RobustLevelHeightMap(raw, source.Width, source.Height, 3);
            var lineCorrected = RemoveScanLineNoise(leveled, source.Width, source.Height, 0.48, out _);
            estimatedNoiseSigma = EstimateNoiseSigma(lineCorrected, source.Width, source.Height);
            var correctedBase = EdgeAwareSmoothHeightMap(lineCorrected, source.Width, source.Height, Math.Max(0.05, estimatedNoiseSigma), 1.2, 2);
            var analysisBase = SmoothHeightMap(correctedBase, source.Width, source.Height, 0.12, 2);
            analysis = analysisBase;
            display = new double[correctedBase.Length];
            for (var i = 0; i < correctedBase.Length; i++) display[i] = correctedBase[i] + displayReference;
        }

        var fallbackFullRange = GetDataRange(display);
        var fullRange = calibratedSpm ? SanitizeRange(100, 800, fallbackFullRange) : fallbackFullRange;
        var autoRaw = ComputePercentileRange(display, 0.005, 0.995);
        var autoRange = ClampRange(fullRange, autoRaw.Min, autoRaw.Max);
        var suggestedRange = calibratedSpm
            ? ClampRange(fullRange, 350, 500)
            : RoundDisplayRangeNice(fullRange);
        var displayMode = calibratedSpm ? "fixed" : "auto";

        return new LoadedHeightMap
        {
            Name = source.Name,
            FilePath = source.FilePath,
            Format = source.Format,
            Unit = source.Unit,
            ChannelDisplay = source.ChannelDisplay,
            Width = source.Width,
            Height = source.Height,
            ScanSizeNm = source.ScanSizeNm,
            NmPerPixel = source.NmPerPixel,
            Data = analysis,
            RawData = raw,
            DisplayData = display,
            DisplayRangeFullMin = fullRange.Min,
            DisplayRangeFullMax = fullRange.Max,
            DisplayRangeAutoMin = autoRange.Min,
            DisplayRangeAutoMax = autoRange.Max,
            DisplayRangeSuggestedMin = suggestedRange.Min,
            DisplayRangeSuggestedMax = suggestedRange.Max,
            DefaultDisplayRangeMode = displayMode,
            DisplayReferenceNm = displayReference,
            EstimatedNoiseSigma = estimatedNoiseSigma,
            PreferScientificPreview = source.PreferScientificPreview
        };
    }

    public IReadOnlyList<double> BuildHistogram(double[]? data, double min, double max, int bins = 96)
    {
        if (data is null || data.Length == 0 || bins < 4) return Array.Empty<double>();
        var range = SanitizeRange(min, max, GetDataRange(data));
        var span = Math.Max(1e-9, range.Max - range.Min);
        var histogram = new double[bins];
        var step = Math.Max(1, data.Length / 16384);
        for (var i = 0; i < data.Length; i += step)
        {
            var value = data[i];
            if (!double.IsFinite(value)) continue;
            var t = StatisticsAndGeometry.Clamp((value - range.Min) / span, 0, 0.999999);
            histogram[(int)Math.Floor(t * bins)]++;
        }

        var maxCount = histogram.Max();
        if (!(maxCount > 0)) return Enumerable.Repeat(0.0, bins).ToArray();
        for (var i = 0; i < histogram.Length; i++) histogram[i] /= maxCount;
        return histogram;
    }

    public double GetSliderStep(double min, double max)
    {
        var span = Math.Abs(max - min);
        if (!(span > 0)) return 0.01;
        if (span <= 2) return 0.01;
        if (span <= 20) return 0.05;
        if (span <= 200) return 0.1;
        return Math.Max(0.25, Math.Round(span / 400.0, 2));
    }

    public double RangePercent(double value, double min, double max)
    {
        var range = SanitizeRange(min, max, (0, 1));
        return StatisticsAndGeometry.Clamp((value - range.Min) / Math.Max(1e-9, range.Max - range.Min) * 100.0, 0, 100);
    }

    public (double Min, double Max) ClampRange((double Min, double Max) bounds, double min, double max)
    {
        var fallback = SanitizeRange(bounds.Min, bounds.Max, (0, 1));
        var span = Math.Max(1e-6, fallback.Max - fallback.Min);
        var minGap = Math.Max(GetSliderStep(fallback.Min, fallback.Max), span / 2000.0);
        var lo = StatisticsAndGeometry.Clamp(min, fallback.Min, fallback.Max - minGap);
        var hi = StatisticsAndGeometry.Clamp(max, lo + minGap, fallback.Max);
        if (!(hi > lo))
        {
            hi = Math.Min(fallback.Max, lo + minGap);
            lo = Math.Max(fallback.Min, Math.Min(lo, hi - minGap));
        }

        return (lo, hi);
    }

    public (double Min, double Max) RoundDisplayRangeNice((double Min, double Max) range)
    {
        var safe = SanitizeRange(range.Min, range.Max, (0, 1));
        var step = GetNiceStep(safe.Min, safe.Max);
        return (Math.Floor(safe.Min / step) * step, Math.Ceiling(safe.Max / step) * step);
    }

    private static bool IsCalibratedSpmDisplay(LoadedHeightMap source) =>
        string.Equals(source.Format, "Bruker", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.Unit, "nm", StringComparison.OrdinalIgnoreCase);

    private static (double Min, double Max) SanitizeRange(double min, double max, (double Min, double Max) fallback)
    {
        if (double.IsFinite(min) && double.IsFinite(max) && max > min) return (min, max);
        return fallback.Max > fallback.Min ? fallback : (0, 1);
    }

    private static (double Min, double Max) GetDataRange(IReadOnlyList<double> data)
    {
        if (data.Count == 0) return (0, 1);
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var i = 0; i < data.Count; i++)
        {
            var value = data[i];
            if (!double.IsFinite(value)) continue;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        if (!(max > min)) return (double.IsFinite(min) ? min : 0, (double.IsFinite(min) ? min : 0) + 1);
        return (min, max);
    }

    private static (double Min, double Max) ComputePercentileRange(IReadOnlyList<double> data, double loFraction, double hiFraction)
    {
        var sorted = SampleSortedValues(data, 16384);
        if (sorted.Count == 0) return (0, 1);
        var loIndex = Math.Clamp((int)Math.Floor((sorted.Count - 1) * loFraction), 0, sorted.Count - 1);
        var hiIndex = Math.Clamp((int)Math.Floor((sorted.Count - 1) * hiFraction), 0, sorted.Count - 1);
        var lo = sorted[loIndex];
        var hi = sorted[hiIndex];
        return hi > lo ? (lo, hi) : (sorted[0], sorted[^1] > sorted[0] ? sorted[^1] : sorted[0] + 1);
    }

    private static List<double> SampleSortedValues(IReadOnlyList<double> data, int maxSamples)
    {
        var values = new List<double>(Math.Min(data.Count, maxSamples));
        var step = Math.Max(1, data.Count / Math.Max(1, maxSamples));
        for (var i = 0; i < data.Count; i += step)
        {
            var value = data[i];
            if (double.IsFinite(value)) values.Add(value);
        }
        values.Sort();
        return values;
    }

    private static double GetNiceStep(double min, double max)
    {
        var span = Math.Abs(max - min);
        if (!(span > 0)) return 1;
        if (span >= 120) return 10;
        if (span >= 40) return 5;
        if (span >= 10) return 1;
        if (span >= 2) return 0.5;
        return 0.1;
    }

    private static double EstimateQuantile(IReadOnlyList<double> data, double fraction)
    {
        var sampled = SampleSortedValues(data, 16384);
        if (sampled.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Floor((sampled.Count - 1) * fraction), 0, sampled.Count - 1);
        return sampled[index];
    }

    private static double[] RobustLevelHeightMap(double[] raw, int width, int height, int iterations)
    {
        var coefficients = FitPlaneCoefficients(raw, width, height, null, 42000);
        var residual = SubtractPlane(raw, width, height, coefficients);
        for (var iter = 0; iter < iterations; iter++)
        {
            var sampled = SampleSortedValues(residual, 14000);
            if (sampled.Count == 0) break;
            var low = sampled[Math.Clamp((int)Math.Floor((sampled.Count - 1) * 0.08), 0, sampled.Count - 1)];
            var high = sampled[Math.Clamp((int)Math.Floor((sampled.Count - 1) * 0.68), 0, sampled.Count - 1)];
            coefficients = FitPlaneCoefficients(raw, width, height, (z, i, _, _) => residual[i] >= low && residual[i] <= high, 30000);
            residual = SubtractPlane(raw, width, height, coefficients);
        }
        return residual;
    }

    private static double[] SubtractPlane(double[] raw, int width, int height, (double A, double B, double C) coefficients)
    {
        var output = new double[raw.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                output[index] = raw[index] - (coefficients.A * x + coefficients.B * y + coefficients.C);
            }
        }
        return output;
    }

    private static (double A, double B, double C) FitPlaneCoefficients(double[] raw, int width, int height, Func<double, int, int, int, bool>? selector, int maxSamples)
    {
        var total = width * height;
        var step = Math.Max(1, (int)Math.Floor(Math.Sqrt(total / Math.Max(1.0, maxSamples))));
        double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        var count = 0;

        void Accumulate(bool fullPass)
        {
            for (var y = 0; y < height; y += fullPass ? 1 : step)
            {
                for (var x = 0; x < width; x += fullPass ? 1 : step)
                {
                    var index = y * width + x;
                    var z = raw[index];
                    if (!double.IsFinite(z)) continue;
                    if (!fullPass && selector is not null && !selector(z, index, x, y)) continue;
                    sx += x;
                    sy += y;
                    sz += z;
                    sxx += x * x;
                    syy += y * y;
                    sxy += x * y;
                    sxz += x * z;
                    syz += y * z;
                    count++;
                }
            }
        }

        Accumulate(false);
        if (count < 12)
        {
            sx = sy = sz = sxx = syy = sxy = sxz = syz = 0;
            count = 0;
            Accumulate(true);
        }

        return Solve3x3(
            new[,]
            {
                { sxx, sxy, sx },
                { sxy, syy, sy },
                { sx, sy, count }
            },
            new[] { sxz, syz, sz });
    }

    private static (double A, double B, double C) Solve3x3(double[,] matrix, double[] rhs)
    {
        var a = new double[3, 4];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++) a[row, col] = matrix[row, col];
            a[row, 3] = rhs[row];
        }

        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++)
            {
                if (Math.Abs(a[row, pivot]) > Math.Abs(a[best, pivot])) best = row;
            }

            if (best != pivot)
            {
                for (var col = pivot; col < 4; col++)
                {
                    (a[pivot, col], a[best, col]) = (a[best, col], a[pivot, col]);
                }
            }

            var divisor = a[pivot, pivot];
            if (Math.Abs(divisor) < 1e-12) continue;
            for (var col = pivot; col < 4; col++) a[pivot, col] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                var factor = a[row, pivot];
                if (Math.Abs(factor) < 1e-12) continue;
                for (var col = pivot; col < 4; col++) a[row, col] -= factor * a[pivot, col];
            }
        }

        return (a[0, 3], a[1, 3], a[2, 3]);
    }

    private static double EstimateNoiseSigma(IReadOnlyList<double> data, int width, int height)
    {
        if (data.Count == 0 || width < 3 || height < 3) return 0;
        var xStep = Math.Max(1, width / 96);
        var yStep = Math.Max(1, height / 96);
        var samples = new List<double>();
        for (var y = 1; y < height - 1; y += yStep)
        {
            for (var x = 1; x < width - 1; x += xStep)
            {
                var index = y * width + x;
                var local = 0.25 * (data[index - 1] + data[index + 1] + data[index - width] + data[index + width]);
                samples.Add(Math.Abs(data[index] - local));
            }
        }

        if (samples.Count == 0) return 0;
        samples.Sort();
        return MedianFromSorted(samples) * 1.4826;
    }

    private static double[] RemoveScanLineNoise(IReadOnlyList<double> source, int width, int height, double strength, out double[] rowOffsets)
    {
        rowOffsets = new double[height];
        if (source.Count == 0 || width < 8 || height < 4 || strength <= 0) return source.ToArray();
        var step = Math.Max(1, width / 256);
        for (var y = 0; y < height; y++)
        {
            var samples = new List<double>();
            var row = y * width;
            for (var x = 0; x < width; x += step)
            {
                var value = source[row + x];
                if (double.IsFinite(value)) samples.Add(value);
            }
            samples.Sort();
            rowOffsets[y] = MedianFromSorted(samples);
        }

        var centered = rowOffsets.ToArray();
        Array.Sort(centered);
        var globalMedian = MedianFromSorted(centered);
        for (var i = 0; i < rowOffsets.Length; i++) rowOffsets[i] -= globalMedian;
        var smoothOffsets = Smooth1D(rowOffsets, 0.58, 3);
        var blend = StatisticsAndGeometry.Clamp(strength, 0, 1);
        var output = new double[source.Count];
        for (var y = 0; y < height; y++)
        {
            var correction = smoothOffsets[y] * blend;
            var row = y * width;
            for (var x = 0; x < width; x++) output[row + x] = source[row + x] - correction;
        }
        rowOffsets = smoothOffsets;
        return output;
    }

    private static double[] EdgeAwareSmoothHeightMap(IReadOnlyList<double> source, int width, int height, double sigma, double strength, int passes)
    {
        if (source.Count == 0) return Array.Empty<double>();
        var input = source.ToArray();
        var output = new double[input.Length];
        var normalized = StatisticsAndGeometry.Clamp(strength / 6.0, 0, 1.25);
        var blend = StatisticsAndGeometry.Clamp(0.05 + 0.58 * normalized, 0, 0.72);
        var scaleSquared = Math.Max(1e-6, Math.Pow(Math.Max(1e-6, sigma) * (1.45 + 3.25 * normalized), 2));
        for (var pass = 0; pass < Math.Max(1, passes); pass++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    var center = input[index];
                    var sum = center * 2;
                    var total = 2.0;
                    AccumulateNeighbour(x - 1, y);
                    AccumulateNeighbour(x + 1, y);
                    AccumulateNeighbour(x, y - 1);
                    AccumulateNeighbour(x, y + 1);
                    var local = sum / Math.Max(1e-6, total);
                    output[index] = center * (1 - blend) + local * blend;

                    void AccumulateNeighbour(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                        var value = input[ny * width + nx];
                        var weight = 1.0 / (1.0 + (value - center) * (value - center) / scaleSquared);
                        sum += value * weight;
                        total += weight;
                    }
                }
            }

            (input, output) = (output, input);
        }

        return input;
    }

    private static double[] SmoothHeightMap(IReadOnlyList<double> source, int width, int height, double alpha, int passes)
    {
        if (source.Count == 0) return Array.Empty<double>();
        var input = source.ToArray();
        var output = new double[input.Length];
        for (var pass = 0; pass < Math.Max(1, passes); pass++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    var sum = input[index];
                    var count = 1;
                    if (x > 0) { sum += input[index - 1]; count++; }
                    if (x + 1 < width) { sum += input[index + 1]; count++; }
                    if (y > 0) { sum += input[index - width]; count++; }
                    if (y + 1 < height) { sum += input[index + width]; count++; }
                    var neighbourMean = sum / count;
                    output[index] = input[index] * (1 - alpha) + neighbourMean * alpha;
                }
            }

            (input, output) = (output, input);
        }

        return input;
    }

    private static double[] Smooth1D(IReadOnlyList<double> source, double alpha, int passes)
    {
        var input = source.ToArray();
        var output = new double[input.Length];
        for (var pass = 0; pass < Math.Max(1, passes); pass++)
        {
            for (var i = 0; i < input.Length; i++)
            {
                var sum = input[i];
                var count = 1;
                if (i > 0) { sum += input[i - 1]; count++; }
                if (i + 1 < input.Length) { sum += input[i + 1]; count++; }
                var neighbourMean = sum / count;
                output[i] = input[i] * (1 - alpha) + neighbourMean * alpha;
            }
            (input, output) = (output, input);
        }
        return input;
    }

    private static double MedianFromSorted(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
