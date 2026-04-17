using System.Text.Json;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class SupervisedGrowthModel
{
    public int ExampleCount { get; init; }
    public double[] FeatureMeans { get; init; } = Array.Empty<double>();
    public double[] FeatureScales { get; init; } = Array.Empty<double>();
    public double[] TargetMeans { get; init; } = Array.Empty<double>();
    public double[] TargetScales { get; init; } = Array.Empty<double>();
    public double[] TargetMins { get; init; } = Array.Empty<double>();
    public double[] TargetMaxs { get; init; } = Array.Empty<double>();
    public double[,] Weights { get; init; } = new double[0, 0];
    public double BlendWeight { get; init; }
}

public readonly record struct GrowthPredictionContext(
    double Progress01,
    double Stage01,
    double IsControl,
    double IsTreated,
    double DoseLog1p,
    double MeanHeightNm,
    double MeanWidthNm,
    double HeightWidthRatio,
    double PeakSeparationNm,
    double DipDepthNm,
    double RoughnessNm,
    double Continuity);

public sealed class SupervisedGrowthLearningService
{
    private const int FeatureVersion = 2;
    private const int TargetVersion = 1;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };
    private readonly Dictionary<string, PersistedGrowthExample> _examples;

    public SupervisedGrowthLearningService()
    {
        _examples = LoadStore().Examples
            .Where(example => example.FeatureVersion == FeatureVersion && example.TargetVersion == TargetVersion)
            .ToDictionary(example => example.Key, StringComparer.OrdinalIgnoreCase);
    }

    private string StorePath
    {
        get
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PieCrustAnalyser");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "supervised-growth-training.json");
        }
    }

    public SupervisedGrowthModel? RefreshModel(IEnumerable<PiecrustFileState> files)
    {
        var candidates = BuildExamples(files).ToArray();
        var changed = false;
        foreach (var candidate in candidates)
        {
            if (_examples.TryGetValue(candidate.Key, out var existing) && ExamplesEquivalent(existing, candidate)) continue;
            _examples[candidate.Key] = candidate;
            changed = true;
        }

        if (changed) SaveStore();

        var usable = _examples.Values
            .Where(example => example.Features.Length > 0 && example.Targets.Length == 5 && IsFiniteArray(example.Features) && IsFiniteArray(example.Targets))
            .ToArray();
        return Train(usable);
    }

    public string DescribeModel(SupervisedGrowthModel? model)
    {
        if (model is null)
        {
            return _examples.Count == 0
                ? "Supervised ML status: no learned examples yet. Run guided extraction on ordered files and the app will start accumulating growth examples locally."
                : $"Supervised ML status: {_examples.Count} stored example(s), but that is not enough yet for a stable learned growth model. Add at least 3 guided ordered references.";
        }

        return $"Supervised ML status: learned from {model.ExampleCount} accumulated example(s) stored locally. The polynomial surface fit is now being nudged by a supervised profile-growth model with blend weight {model.BlendWeight:F2}.";
    }

    public static double[] PredictShape(SupervisedGrowthModel model, GrowthPredictionContext context)
    {
        if (model.Weights.Length == 0) return Array.Empty<double>();
        var features = BuildFeatureVector(context);
        if (features.Length != model.FeatureMeans.Length) return Array.Empty<double>();

        var augmented = new double[features.Length + 1];
        augmented[0] = 1;
        for (var i = 0; i < features.Length; i++)
        {
            augmented[i + 1] = (features[i] - model.FeatureMeans[i]) / Math.Max(1e-9, model.FeatureScales[i]);
        }

        var outputs = new double[model.TargetMeans.Length];
        for (var targetIndex = 0; targetIndex < outputs.Length; targetIndex++)
        {
            double sum = 0;
            for (var featureIndex = 0; featureIndex < augmented.Length; featureIndex++)
            {
                sum += model.Weights[featureIndex, targetIndex] * augmented[featureIndex];
            }

            var value = model.TargetMeans[targetIndex] + sum * model.TargetScales[targetIndex];
            var min = model.TargetMins[targetIndex];
            var max = model.TargetMaxs[targetIndex];
            outputs[targetIndex] = StatisticsAndGeometry.Clamp(value, min, max);
        }

        return outputs;
    }

    private IEnumerable<PersistedGrowthExample> BuildExamples(IEnumerable<PiecrustFileState> files)
    {
        var stagedFiles = files
            .Where(file => file.GuidedSummary is not null && file.EvolutionRecord?.Profile is { Length: > 15 })
            .OrderBy(file => file.SequenceOrder)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stagedFiles.Length == 0) yield break;

        var progressLookup = BuildProgressLookup(stagedFiles);
        foreach (var file in stagedFiles)
        {
            var progress01 = progressLookup.TryGetValue(file.FilePath, out var progress) ? progress : StageTo01(file.Stage);
            var summary = file.GuidedSummary!;
            var targets = ExtractBimodalTargets(file.EvolutionRecord!.Profile);
            if (targets.Length != 5 || !IsFiniteArray(targets)) continue;

            yield return new PersistedGrowthExample
            {
                Key = BuildKey(file.FilePath),
                FeatureVersion = FeatureVersion,
                TargetVersion = TargetVersion,
                Features = BuildFeatureVector(new GrowthPredictionContext(
                    progress01,
                    progress01,
                    string.Equals(file.ConditionType, "control", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    string.Equals(file.ConditionType, "treated", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    Math.Log(1 + Math.Max(0, file.AntibioticDoseUgPerMl)),
                    summary.MeanHeightNm,
                    summary.MeanWidthNm,
                    summary.HeightToWidthRatio,
                    summary.PeakSeparationNm,
                    summary.DipDepthNm,
                    summary.RoughnessNm,
                    summary.Continuity)),
                Targets = targets
            };
        }
    }

    private GrowthExampleStore LoadStore()
    {
        try
        {
            if (!File.Exists(StorePath)) return new GrowthExampleStore();
            var json = File.ReadAllText(StorePath);
            return string.IsNullOrWhiteSpace(json)
                ? new GrowthExampleStore()
                : JsonSerializer.Deserialize<GrowthExampleStore>(json, _jsonOptions) ?? new GrowthExampleStore();
        }
        catch
        {
            return new GrowthExampleStore();
        }
    }

    private void SaveStore()
    {
        try
        {
            var store = new GrowthExampleStore { Examples = _examples.Values.ToList() };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(store, _jsonOptions));
        }
        catch
        {
            // Keep the app usable even if local ML persistence fails.
        }
    }

    private static SupervisedGrowthModel? Train(IReadOnlyList<PersistedGrowthExample> examples)
    {
        if (examples.Count < 3) return null;

        var featureCount = examples[0].Features.Length;
        var targetCount = examples[0].Targets.Length;
        if (featureCount == 0 || targetCount == 0) return null;

        var featureMeans = new double[featureCount];
        var featureScales = new double[featureCount];
        var targetMeans = new double[targetCount];
        var targetScales = new double[targetCount];
        var targetMins = new double[targetCount];
        var targetMaxs = new double[targetCount];

        for (var i = 0; i < featureCount; i++)
        {
            var values = examples.Select(example => example.Features[i]).ToArray();
            featureMeans[i] = StatisticsAndGeometry.Mean(values);
            featureScales[i] = Math.Max(1e-6, StatisticsAndGeometry.StandardDeviation(values));
        }

        for (var i = 0; i < targetCount; i++)
        {
            var values = examples.Select(example => example.Targets[i]).ToArray();
            targetMeans[i] = StatisticsAndGeometry.Mean(values);
            targetScales[i] = Math.Max(1e-6, StatisticsAndGeometry.StandardDeviation(values));
            targetMins[i] = values.Min();
            targetMaxs[i] = values.Max();
        }

        var rows = examples.Count;
        var cols = featureCount + 1;
        var design = new double[rows, cols];
        var standardizedTargets = new double[rows, targetCount];

        for (var row = 0; row < rows; row++)
        {
            design[row, 0] = 1;
            for (var featureIndex = 0; featureIndex < featureCount; featureIndex++)
            {
                design[row, featureIndex + 1] = (examples[row].Features[featureIndex] - featureMeans[featureIndex]) / featureScales[featureIndex];
            }

            for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                standardizedTargets[row, targetIndex] = (examples[row].Targets[targetIndex] - targetMeans[targetIndex]) / targetScales[targetIndex];
            }
        }

        var xtx = new double[cols, cols];
        var xty = new double[cols, targetCount];
        for (var row = 0; row < rows; row++)
        {
            for (var i = 0; i < cols; i++)
            {
                for (var j = 0; j < cols; j++) xtx[i, j] += design[row, i] * design[row, j];
                for (var targetIndex = 0; targetIndex < targetCount; targetIndex++) xty[i, targetIndex] += design[row, i] * standardizedTargets[row, targetIndex];
            }
        }

        var lambda = 0.25;
        for (var i = 1; i < cols; i++) xtx[i, i] += lambda;

        var inverse = InvertSmallMatrix(xtx);
        var weights = Multiply(inverse, xty);
        var blendWeight = StatisticsAndGeometry.Clamp(0.12 + Math.Log2(examples.Count + 1) * 0.05, 0.12, 0.40);
        return new SupervisedGrowthModel
        {
            ExampleCount = examples.Count,
            FeatureMeans = featureMeans,
            FeatureScales = featureScales,
            TargetMeans = targetMeans,
            TargetScales = targetScales,
            TargetMins = targetMins,
            TargetMaxs = targetMaxs,
            Weights = weights,
            BlendWeight = blendWeight
        };
    }

    private static Dictionary<string, double> BuildProgressLookup(IReadOnlyList<PiecrustFileState> files)
    {
        var lookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return lookup;

        var minSequence = files.Min(file => file.SequenceOrder);
        var maxSequence = files.Max(file => file.SequenceOrder);
        for (var i = 0; i < files.Count; i++)
        {
            var orderProgress = files.Count == 1 ? 0.5 : i / (double)(files.Count - 1);
            var sequenceProgress = maxSequence == minSequence
                ? orderProgress
                : (files[i].SequenceOrder - minSequence) / (double)Math.Max(1, maxSequence - minSequence);
            lookup[files[i].FilePath] = StatisticsAndGeometry.Clamp(sequenceProgress, 0, 1);
        }

        return lookup;
    }

    private static string BuildKey(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
    }

    private static bool ExamplesEquivalent(PersistedGrowthExample a, PersistedGrowthExample b)
    {
        if (a.FeatureVersion != b.FeatureVersion || a.TargetVersion != b.TargetVersion) return false;
        if (a.Features.Length != b.Features.Length || a.Targets.Length != b.Targets.Length) return false;
        return AreClose(a.Features, b.Features) && AreClose(a.Targets, b.Targets);
    }

    private static bool AreClose(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
        }

        return true;
    }

    private static double[] BuildFeatureVector(GrowthPredictionContext context)
    {
        var progress = StatisticsAndGeometry.Clamp(context.Progress01, 0, 1);
        return
        [
            progress,
            progress * progress,
            progress * progress * progress,
            StatisticsAndGeometry.Clamp(context.Stage01, 0, 1),
            StatisticsAndGeometry.Clamp(context.IsControl, 0, 1),
            StatisticsAndGeometry.Clamp(context.IsTreated, 0, 1),
            Math.Max(0, context.DoseLog1p),
            Math.Max(0, context.MeanHeightNm),
            Math.Max(0, context.MeanWidthNm),
            Math.Max(0, context.HeightWidthRatio),
            Math.Max(0, context.PeakSeparationNm),
            Math.Max(0, context.DipDepthNm),
            Math.Max(0, context.RoughnessNm),
            StatisticsAndGeometry.Clamp(context.Continuity, 0, 1)
        ];
    }

    private static double[] ExtractBimodalTargets(IReadOnlyList<double> profile)
    {
        if (profile.Count == 0) return Array.Empty<double>();
        var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(profile.ToArray(), 2);
        var corrected = ShiftToZero(BaselineCorrect(smoothed));
        if (corrected.Length < 6) return Array.Empty<double>();

        var mid = corrected.Length / 2;
        var left = corrected.Take(mid).ToArray();
        var right = corrected.Skip(mid).ToArray();
        var leftFit = FitHalf(left, 0);
        var rightFit = FitHalf(right, mid);
        var separation01 = Math.Abs(rightFit.Mu - leftFit.Mu) / Math.Max(1.0, corrected.Length - 1.0);
        return
        [
            Math.Max(0, leftFit.Amplitude),
            StatisticsAndGeometry.Clamp(leftFit.Sigma / Math.Max(1.0, corrected.Length - 1.0), 0.01, 0.35),
            Math.Max(0, rightFit.Amplitude),
            StatisticsAndGeometry.Clamp(rightFit.Sigma / Math.Max(1.0, corrected.Length - 1.0), 0.01, 0.35),
            StatisticsAndGeometry.Clamp(separation01, 0.02, 0.90)
        ];
    }

    private static double[] BaselineCorrect(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var edge = Math.Max(2, (int)Math.Floor(values.Count * 0.12));
        var baseline = (StatisticsAndGeometry.Mean(values.Take(edge).ToArray()) + StatisticsAndGeometry.Mean(values.Skip(values.Count - edge).Take(edge).ToArray())) / 2.0;
        var corrected = new double[values.Count];
        for (var i = 0; i < values.Count; i++) corrected[i] = Math.Max(0, values[i] - baseline);
        return corrected;
    }

    private static double[] ShiftToZero(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var min = values.Min();
        var output = new double[values.Count];
        for (var i = 0; i < values.Count; i++) output[i] = Math.Max(0, values[i] - min);
        return output;
    }

    private static (double Amplitude, double Mu, double Sigma) FitHalf(IReadOnlyList<double> values, int offset)
    {
        if (values.Count == 0) return (0, offset, 1);
        var array = values.ToArray();
        var max = array.Max();
        var maxIndex = Array.IndexOf(array, max);
        var half = max / 2.0;
        var lo = maxIndex;
        var hi = maxIndex;
        while (lo > 0 && array[lo] > half) lo--;
        while (hi < array.Length - 1 && array[hi] > half) hi++;
        var fwhm = Math.Max(1, hi - lo);
        var sigma = fwhm / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)));
        return (max, maxIndex + offset, Math.Max(0.5, sigma));
    }

    private static bool IsFiniteArray(IReadOnlyList<double> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i])) return false;
        }

        return true;
    }

    private static double[,] Multiply(double[,] left, double[,] right)
    {
        var rows = left.GetLength(0);
        var cols = right.GetLength(1);
        var inner = left.GetLength(1);
        var output = new double[rows, cols];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < cols; column++)
            {
                double sum = 0;
                for (var k = 0; k < inner; k++) sum += left[row, k] * right[k, column];
                output[row, column] = sum;
            }
        }

        return output;
    }

    private static double[,] InvertSmallMatrix(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var augmented = new double[n, n * 2];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) augmented[i, j] = matrix[i, j];
            augmented[i, i + n] = 1;
        }

        for (var pivot = 0; pivot < n; pivot++)
        {
            var bestRow = pivot;
            var bestMagnitude = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < n; row++)
            {
                var magnitude = Math.Abs(augmented[row, pivot]);
                if (magnitude <= bestMagnitude) continue;
                bestMagnitude = magnitude;
                bestRow = row;
            }

            if (bestRow != pivot)
            {
                for (var column = 0; column < n * 2; column++)
                {
                    (augmented[pivot, column], augmented[bestRow, column]) = (augmented[bestRow, column], augmented[pivot, column]);
                }
            }

            var divisor = Math.Abs(augmented[pivot, pivot]) < 1e-12 ? 1e-12 : augmented[pivot, pivot];
            for (var column = 0; column < n * 2; column++) augmented[pivot, column] /= divisor;

            for (var row = 0; row < n; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                if (Math.Abs(factor) < 1e-12) continue;
                for (var column = 0; column < n * 2; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        var inverse = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) inverse[i, j] = augmented[i, j + n];
        }

        return inverse;
    }

    private static double StageTo01(string stage) => stage switch
    {
        "early" => 0.15,
        "middle" => 0.55,
        "late" => 0.90,
        _ => 0.50
    };

    private sealed class GrowthExampleStore
    {
        public List<PersistedGrowthExample> Examples { get; set; } = new();
    }

    private sealed class PersistedGrowthExample
    {
        public string Key { get; set; } = string.Empty;
        public int FeatureVersion { get; set; }
        public int TargetVersion { get; set; }
        public double[] Features { get; set; } = Array.Empty<double>();
        public double[] Targets { get; set; } = Array.Empty<double>();
    }
}
