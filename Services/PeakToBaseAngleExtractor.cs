using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public static class PeakToBaseAngleExtractor
{
    public static IReadOnlyList<PeakToBaseAngleMeasurement> Extract(
        IReadOnlyList<double> values,
        IReadOnlyList<double> offsetsNm,
        string flankMode,
        double thresholdFraction,
        double maxBaseDistanceNm)
    {
        if (values.Count < 5 || values.Count != offsetsNm.Count) return Array.Empty<PeakToBaseAngleMeasurement>();
        thresholdFraction = StatisticsAndGeometry.Clamp(thresholdFraction, 0.01, 0.40);
        maxBaseDistanceNm = Math.Max(1, maxBaseDistanceNm);
        var window = Math.Clamp((values.Count / 18) | 1, 5, Math.Min(21, values.Count % 2 == 1 ? values.Count : values.Count - 1));
        var smoothed = StatisticsAndGeometry.SavitzkyGolaySmooth(values, window, 2);
        var peakIndex = FindPeak(smoothed);
        var flanks = NormalizeFlankMode(flankMode) switch
        {
            "left" => new[] { "left" },
            "right" => new[] { "right" },
            _ => new[] { "left", "right" }
        };

        return flanks
            .Select(flank => ExtractOne(smoothed, offsetsNm, peakIndex, flank, thresholdFraction, maxBaseDistanceNm))
            .Where(result => result is not null)
            .Cast<PeakToBaseAngleMeasurement>()
            .ToArray();
    }

    public static int FindPeak(IReadOnlyList<double> profile)
    {
        if (profile.Count == 0) return 0;
        var best = 0;
        var bestValue = profile[0];
        for (var i = 1; i < profile.Count; i++)
        {
            if (!(profile[i] > bestValue)) continue;
            best = i;
            bestValue = profile[i];
        }
        return best;
    }

    public static double ComputeAngle(double peakX, double peakZ, double baseX, double baseZ)
    {
        var dx = Math.Abs(peakX - baseX);
        var dz = peakZ - baseZ;
        var acuteSurfaceAngle = Math.Atan2(Math.Max(0, dz), Math.Max(1e-9, dx));
        return Math.PI - acuteSurfaceAngle;
    }

    private static PeakToBaseAngleMeasurement? ExtractOne(
        IReadOnlyList<double> profile,
        IReadOnlyList<double> offsetsNm,
        int peakIndex,
        string flank,
        double thresholdFraction,
        double maxBaseDistanceNm)
    {
        var peakZ = profile[peakIndex];
        var peakX = offsetsNm[peakIndex];
        var baseline = EstimateLocalBaseline(profile, flank);
        var target = baseline + thresholdFraction * Math.Max(0, peakZ - baseline);
        var search = flank == "left"
            ? Enumerable.Range(0, peakIndex).Reverse()
            : Enumerable.Range(peakIndex + 1, Math.Max(0, profile.Count - peakIndex - 1));

        int? baseIndex = null;
        foreach (var index in search)
        {
            if (Math.Abs(offsetsNm[index] - peakX) > maxBaseDistanceNm) break;
            if (profile[index] <= target)
            {
                baseIndex = index;
                break;
            }
        }

        if (baseIndex is null)
        {
            var candidates = flank == "left"
                ? Enumerable.Range(0, peakIndex)
                : Enumerable.Range(peakIndex + 1, Math.Max(0, profile.Count - peakIndex - 1));
            baseIndex = candidates
                .Where(index => Math.Abs(offsetsNm[index] - peakX) <= maxBaseDistanceNm)
                .OrderBy(index => Math.Abs(profile[index] - target))
                .Select(index => (int?)index)
                .FirstOrDefault();
        }

        if (baseIndex is null) return null;
        var selected = baseIndex.Value;
        if (selected == peakIndex) return null;
        var distance = Math.Abs(offsetsNm[selected] - peakX);
        var lowConfidence = distance > maxBaseDistanceNm || distance <= 1e-9;
        var confidence = lowConfidence
            ? 0.25
            : StatisticsAndGeometry.Clamp(1.0 - Math.Abs(profile[selected] - target) / Math.Max(1e-9, peakZ - baseline), 0.25, 1.0);
        return new PeakToBaseAngleMeasurement
        {
            Flank = flank,
            PeakXNm = peakX,
            PeakZNm = peakZ,
            BaselineNm = baseline,
            BaseXNm = offsetsNm[selected],
            BaseZNm = profile[selected],
            AngleRad = ComputeAngle(peakX, peakZ, offsetsNm[selected], profile[selected]),
            Confidence = confidence,
            LowConfidence = lowConfidence
        };
    }

    private static double EstimateLocalBaseline(IReadOnlyList<double> profile, string flank)
    {
        var n = Math.Max(2, (int)Math.Ceiling(profile.Count * 0.16));
        var samples = flank == "left" ? profile.Take(n).ToArray() : profile.Skip(Math.Max(0, profile.Count - n)).ToArray();
        Array.Sort(samples);
        return samples.Length == 0 ? 0 : samples[samples.Length / 2];
    }

    private static string NormalizeFlankMode(string mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "left" or "right" ? normalized : "both";
    }
}

public sealed class AngleHeightPredictionModel
{
    public string FitType { get; init; } = "polynomial2";
    public double[] Coefficients { get; init; } = Array.Empty<double>();
    public double[] SampleHeights { get; init; } = Array.Empty<double>();
    public double[] SampleAngles { get; init; } = Array.Empty<double>();
    public double MinHeight { get; init; }
    public double MaxHeight { get; init; } = 1;
    public double MinAngle { get; init; }
    public double MaxAngle { get; init; } = 1;

    public double PredictAngleDeg(double height)
    {
        var x = Math.Max(0, height);
        if (FitType == "spline" && SampleHeights.Length > 0) return PredictSplineLikeAngle(x);
        if (Coefficients.Length == 0) return 0;
        var y = 0.0;
        var power = 1.0;
        for (var i = 0; i < Coefficients.Length; i++)
        {
            y += Coefficients[i] * power;
            power *= x;
        }
        // Enforce monotone non-increasing: the angle must never exceed the value at MinHeight.
        // A degree-2 polynomial has a turning point; if it curves back upward at high heights
        // that is physically wrong (piecrust flanks can only steepen or stay flat with growth).
        if (Coefficients.Length > 1 && MinHeight >= 0)
        {
            var yAtMin = 0.0;
            var pMin = 1.0;
            var xMin = Math.Max(0.0, MinHeight);
            for (var i = 0; i < Coefficients.Length; i++) { yAtMin += Coefficients[i] * pMin; pMin *= xMin; }
            y = Math.Min(y, yAtMin);
        }
        return StatisticsAndGeometry.Clamp(y, 90.0, Math.Max(90.0, MaxAngle));
    }

    public double NormalizeAngle(double angleDeg) =>
        StatisticsAndGeometry.Clamp((Math.Max(90.0, MaxAngle) - angleDeg) / Math.Max(1e-9, Math.Max(90.0, MaxAngle) - 90.0), 0, 1);

    private double PredictSplineLikeAngle(double height)
    {
        if (SampleHeights.Length == 1) return SampleAngles[0];
        var upper = Array.FindIndex(SampleHeights, h => h >= height);
        if (upper <= 0) return SampleAngles[0];
        if (upper < 0) return SampleAngles[^1];
        var lower = upper - 1;
        var span = Math.Max(1e-9, SampleHeights[upper] - SampleHeights[lower]);
        var t = StatisticsAndGeometry.Clamp((height - SampleHeights[lower]) / span, 0, 1);
        var smoothT = t * t * (3 - 2 * t);
        var y = SampleAngles[lower] * (1 - smoothT) + SampleAngles[upper] * smoothT;
        return StatisticsAndGeometry.Clamp(y, 90.0, Math.Max(90.0, MaxAngle));
    }
}

public static class AngleInformedFuturePredictor
{
    public static AngleHeightPredictionModel FitAngleHeightModel(IEnumerable<(double Height, double AngleDeg)> data, string fitType)
    {
        var samples = data
            .Where(sample => double.IsFinite(sample.Height) && double.IsFinite(sample.AngleDeg) && sample.Height > 0 && sample.AngleDeg > 0)
            .OrderBy(sample => sample.Height)
            .ToArray();
        if (samples.Length == 0) return new AngleHeightPredictionModel();
        var normalizedFit = NormalizeFitType(fitType);
        var degree = normalizedFit == "linear" || samples.Length < 3 ? 1 : 2;
        var coefficients = FitPolynomial(samples.Select(s => s.Height).ToArray(), samples.Select(s => s.AngleDeg).ToArray(), degree);
        return new AngleHeightPredictionModel
        {
            FitType = normalizedFit,
            Coefficients = coefficients,
            SampleHeights = samples.Select(s => s.Height).ToArray(),
            SampleAngles = samples.Select(s => s.AngleDeg).ToArray(),
            MinHeight = samples.Min(s => s.Height),
            MaxHeight = Math.Max(samples.Max(s => s.Height), samples.Min(s => s.Height) + 1e-6),
            MinAngle = samples.Min(s => s.AngleDeg),
            MaxAngle = Math.Max(samples.Max(s => s.AngleDeg), samples.Min(s => s.AngleDeg) + 1e-6)
        };
    }

    public static double GrowthFactor(double height, AngleHeightPredictionModel model, double wAngle)
    {
        var angle = model.PredictAngleDeg(height);
        var rightAngleApproach = model.NormalizeAngle(angle);
        return StatisticsAndGeometry.Clamp(1.0 + Math.Max(0, wAngle) * rightAngleApproach, 0.75, 1.85);
    }

    public static double SigmoidPhase(double t, double tau, double delta)
    {
        var safeDelta = Math.Max(1e-6, Math.Abs(delta));
        var x = StatisticsAndGeometry.Clamp((t - tau) / safeDelta, -60, 60);
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    public static double[] SmoothByCurvature(IReadOnlyList<double> values, int width, int height, double beta, int passes = 1)
    {
        if (values.Count == 0 || width <= 2 || height <= 2 || beta <= 0) return values.ToArray();
        var input = values.ToArray();
        var output = new double[input.Length];
        var blend = StatisticsAndGeometry.Clamp(beta, 0, 0.25);
        for (var pass = 0; pass < Math.Max(1, passes); pass++)
        {
            Array.Copy(input, output, input.Length);
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    var neighbourMean = (input[index - 1] + input[index + 1] + input[index - width] + input[index + width]) / 4.0;
                    output[index] = Math.Max(0, input[index] * (1 - blend) + neighbourMean * blend);
                }
            }

            (input, output) = (output, input);
        }

        return input;
    }

    private static string NormalizeFitType(string fitType)
    {
        var normalized = (fitType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "linear" or "spline" ? normalized : "polynomial2";
    }

    private static double[] FitPolynomial(IReadOnlyList<double> x, IReadOnlyList<double> y, int degree)
    {
        var n = degree + 1;
        var matrix = new double[n, n + 1];
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
            {
                matrix[row, col] = x.Sum(v => Math.Pow(v, row + col));
            }
            matrix[row, n] = x.Select((v, i) => y[i] * Math.Pow(v, row)).Sum();
        }

        for (var pivot = 0; pivot < n; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            }
            if (best != pivot)
            {
                for (var col = pivot; col <= n; col++) (matrix[pivot, col], matrix[best, col]) = (matrix[best, col], matrix[pivot, col]);
            }
            var div = matrix[pivot, pivot];
            if (Math.Abs(div) < 1e-12) continue;
            for (var col = pivot; col <= n; col++) matrix[pivot, col] /= div;
            for (var row = 0; row < n; row++)
            {
                if (row == pivot) continue;
                var factor = matrix[row, pivot];
                for (var col = pivot; col <= n; col++) matrix[row, col] -= factor * matrix[pivot, col];
            }
        }

        return Enumerable.Range(0, n).Select(i => matrix[i, n]).ToArray();
    }
}
