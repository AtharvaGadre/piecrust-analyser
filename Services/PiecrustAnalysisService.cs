using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class PiecrustAnalysisService
{
    public static readonly string[] StageOrder = ["early", "middle", "late"];
    private sealed record GuideAlignmentInfo(double RotationRadians, PointD GuideCenterNm, double MinXNm, double MaxXNm, double MinYNm, double MaxYNm, double BackgroundValue);

    public IReadOnlyList<PlotPoint> BuildLineProfile(PiecrustFileState file)
    {
        var sourceData = file.RawHeightData.Length > 0
            ? file.RawHeightData
            : file.HeightData.Length > 0
                ? file.HeightData
                : file.DisplayHeightData;
        if (file.ProfileLine.Count < 2 || sourceData.Length == 0) return Array.Empty<PlotPoint>();
        var p1 = file.ProfileLine[0];
        var p2 = file.ProfileLine[1];
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        var distancePx = Math.Sqrt(dx * dx + dy * dy);
        if (!(distancePx > 1e-8)) return Array.Empty<PlotPoint>();

        var tangentX = dx / distancePx;
        var tangentY = dy / distancePx;
        var normalX = -tangentY;
        var normalY = tangentX;
        var oversamplePerPixel = 8.0;
        var normalHalfWidthPx = 2.25;
        var normalSamples = 9;
        var steps = Math.Max(96, (int)Math.Ceiling(distancePx * oversamplePerPixel));
        var distancesNm = new double[steps + 1];
        var values = new double[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = p1.X + dx * t;
            var y = p1.Y + dy * t;
            distancesNm[i] = t * distancePx * file.NmPerPixel;

            double weightedSum = 0;
            double weightSum = 0;
            for (var sample = 0; sample < normalSamples; sample++)
            {
                var frac = normalSamples == 1 ? 0 : sample / (double)(normalSamples - 1) * 2 - 1;
                var offset = frac * normalHalfWidthPx;
                var sigma = Math.Max(0.35, normalHalfWidthPx * 0.65);
                var weight = Math.Exp(-(offset * offset) / (2 * sigma * sigma));
                var sampleX = x + normalX * offset;
                var sampleY = y + normalY * offset;
                weightedSum += StatisticsAndGeometry.BilinearClamped(sourceData, file.PixelWidth, file.PixelHeight, sampleX, sampleY) * weight;
                weightSum += weight;
            }

            values[i] = weightSum > 0
                ? weightedSum / weightSum
                : StatisticsAndGeometry.BilinearClamped(sourceData, file.PixelWidth, file.PixelHeight, x, y);
        }

        var gaussianPreSmooth = StatisticsAndGeometry.MovingGaussianSmooth(values, 3);
        var baseWindow = Math.Max(17, 2 * ((steps + 1) / 18) + 1);
        var firstWindow = Math.Min(51, baseWindow % 2 == 1 ? baseWindow : baseWindow + 1);
        var secondWindowCandidate = firstWindow - 8;
        if (secondWindowCandidate % 2 == 0) secondWindowCandidate--;
        var secondWindow = Math.Max(11, secondWindowCandidate);
        var smooth1 = StatisticsAndGeometry.SavitzkyGolaySmooth(gaussianPreSmooth, firstWindow, 3);
        var smooth2 = StatisticsAndGeometry.SavitzkyGolaySmooth(smooth1, secondWindow, 3);

        var profile = new PlotPoint[steps + 1];
        for (var i = 0; i <= steps; i++) profile[i] = new PlotPoint(distancesNm[i], smooth2[i]);
        return profile;
    }

    public EquationDiscoveryProfileInput? BuildEquationDiscoveryProfileInput(PiecrustFileState file)
    {
        if (!HasUsableGuide(file) || file.HeightData.Length == 0) return null;
        var guidedPerpendicularProfiles = BuildGuidedPerpendicularProfilesForDiscovery(file, 10, 0.20, 1.0);
        var sampled = StatisticsAndGeometry.SampleCurveAtPhysicalInterval(file.GuidePoints, 1.0, file.NmPerPixel);
        if (sampled.Count < 8) return null;

        var xNm = new double[sampled.Count];
        var yNm = new double[sampled.Count];
        var sNm = new double[sampled.Count];
        var zNm = new double[sampled.Count];
        var bandHalfWidthPx = Math.Max(0.75, Math.Min(6.0, file.GuideCorridorWidthNm / Math.Max(1e-9, file.NmPerPixel) * 0.12));
        var bandSamples = 7;

        for (var i = 0; i < sampled.Count; i++)
        {
            var point = sampled[i].Point;
            var tangent = StatisticsAndGeometry.GetCurveTangent(sampled, i);
            var normal = new PointD(-tangent.Y, tangent.X);
            double weightedSum = 0;
            double weightSum = 0;
            for (var sample = 0; sample < bandSamples; sample++)
            {
                var frac = bandSamples == 1 ? 0 : sample / (double)(bandSamples - 1) * 2 - 1;
                var offset = frac * bandHalfWidthPx;
                var sigma = Math.Max(0.35, bandHalfWidthPx * 0.65);
                var weight = Math.Exp(-(offset * offset) / (2 * sigma * sigma));
                weightedSum += StatisticsAndGeometry.BilinearClamped(
                    file.HeightData,
                    file.PixelWidth,
                    file.PixelHeight,
                    point.X + normal.X * offset,
                    point.Y + normal.Y * offset) * weight;
                weightSum += weight;
            }

            xNm[i] = point.X * file.NmPerPixel;
            yNm[i] = point.Y * file.NmPerPixel;
            sNm[i] = sampled[i].ArcNm;
            zNm[i] = weightSum > 0 ? weightedSum / weightSum : StatisticsAndGeometry.BilinearClamped(file.HeightData, file.PixelWidth, file.PixelHeight, point.X, point.Y);
        }

        var summary = file.GuidedSummary;
        var compromise = BuildGrowthQuantification(new[] { file }, file)?.CompromiseRatio ?? 0;
        return new EquationDiscoveryProfileInput
        {
            FileName = file.Name,
            FilePath = file.FilePath,
            SequenceOrder = file.SequenceOrder,
            Stage = file.Stage,
            ConditionType = file.ConditionType,
            Unit = file.Unit,
            DoseUgPerMl = file.AntibioticDoseUgPerMl,
            ScanSizeNm = file.ScanSizeNm,
            NmPerPixel = file.NmPerPixel,
            MeanHeightNm = summary?.MeanHeightNm ?? 0,
            MeanWidthNm = summary?.MeanWidthNm ?? 0,
            HeightToWidthRatio = summary?.HeightToWidthRatio ?? 0,
            RoughnessNm = summary?.RoughnessNm ?? 0,
            PeakSeparationNm = summary?.PeakSeparationNm ?? 0,
            DipDepthNm = summary?.DipDepthNm ?? 0,
            CompromiseRatio = compromise,
            XNm = xNm,
            YNm = yNm,
            SNm = sNm,
            ZNm = zNm,
            GuidedPerpendicularProfiles = guidedPerpendicularProfiles
        };
    }

    private IReadOnlyList<EquationDiscoveryGuidedProfileInput> BuildGuidedPerpendicularProfilesForDiscovery(
        PiecrustFileState file,
        int profileCount,
        double widthExpansionFraction,
        double sampleStepNm)
    {
        if (!HasUsableGuide(file) || file.HeightData.Length == 0 || profileCount <= 0)
        {
            return Array.Empty<EquationDiscoveryGuidedProfileInput>();
        }

        var sampled = StatisticsAndGeometry.SampleCurveAtPhysicalInterval(
            file.GuidePoints,
            Math.Max(file.NmPerPixel * 0.5, sampleStepNm),
            file.NmPerPixel);
        if (sampled.Count < 2)
        {
            return Array.Empty<EquationDiscoveryGuidedProfileInput>();
        }

        var guideLengthNm = sampled[^1].ArcNm;
        var halfWidthPx = GetGuideProfileHalfWidthPx(file, widthExpansionFraction);
        var stepPx = Math.Max(0.25, sampleStepNm / Math.Max(1e-9, file.NmPerPixel));
        var outputs = new List<EquationDiscoveryGuidedProfileInput>(profileCount);

        for (var profileIndex = 0; profileIndex < profileCount; profileIndex++)
        {
            var progress = profileCount == 1
                ? 0.5
                : profileIndex / (double)Math.Max(1, profileCount - 1);
            var arcPositionNm = progress * Math.Max(0, guideLengthNm);
            var (point, tangent) = InterpolateGuideFrame(sampled, arcPositionNm);
            var profile = GetPerpendicularProfile(
                file.HeightData,
                file.PixelWidth,
                file.PixelHeight,
                point,
                tangent,
                halfWidthPx,
                stepPx,
                file.NmPerPixel);
            var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(profile.Values, 2);
            var corrected = ShiftToZero(BaselineCorrect(smoothed));

            var normal = new PointD(-tangent.Y, tangent.X);
            var xNm = new double[profile.OffsetsNm.Length];
            var yNm = new double[profile.OffsetsNm.Length];
            for (var i = 0; i < profile.OffsetsNm.Length; i++)
            {
                var offsetPx = profile.OffsetsNm[i] / Math.Max(1e-9, file.NmPerPixel);
                xNm[i] = (point.X + normal.X * offsetPx) * file.NmPerPixel;
                yNm[i] = (point.Y + normal.Y * offsetPx) * file.NmPerPixel;
            }

            outputs.Add(new EquationDiscoveryGuidedProfileInput
            {
                ProfileIndex = profileIndex,
                ArcPositionNm = arcPositionNm,
                XNm = xNm,
                YNm = yNm,
                SNm = profile.OffsetsNm,
                ZNm = corrected
            });
        }

        return outputs;
    }

    public GuidedSummary? ExtractGuidedSummary(PiecrustFileState file)
    {
        if (!file.UseManualGuide || !file.GuideLineFinished || file.GuidePoints.Count < 2 || file.HeightData.Length == 0) return null;
        var sampled = StatisticsAndGeometry.SampleCurveAtPhysicalInterval(file.GuidePoints, 1.0, file.NmPerPixel);
        if (sampled.Count < 2) return null;

        var corridorHalfWidthPx = GetGuideProfileHalfWidthPx(file);
        var widths = new List<double>();
        var heights = new List<double>();
        var rawHeights = new List<double>();
        var ratios = new List<double>();
        var prominences = new List<double>();
        var curvatures = new List<double>();
        var leftPeaks = new List<double>();
        var rightPeaks = new List<double>();
        var separations = new List<double>();
        var metrics = new List<GuidedMetric>(sampled.Count);
        double roughnessAccumulator = 0;
        int roughnessCount = 0;

        for (var i = 0; i < sampled.Count; i++)
        {
            if (i > 0 && i < sampled.Count - 1)
            {
                var t0 = StatisticsAndGeometry.GetCurveTangent(sampled, i - 1);
                var t1 = StatisticsAndGeometry.GetCurveTangent(sampled, i + 1);
                var dot = StatisticsAndGeometry.Clamp(t0.X * t1.X + t0.Y * t1.Y, -1, 1);
                var ds = Math.Max(1e-6, (sampled[i + 1].ArcNm - sampled[i - 1].ArcNm) / 2.0);
                curvatures.Add(Math.Acos(dot) / ds);
            }

            var tangent = StatisticsAndGeometry.GetCurveTangent(sampled, i);
            var rawProfile = GetPerpendicularProfile(file.HeightData, file.PixelWidth, file.PixelHeight, sampled[i].Point, tangent, corridorHalfWidthPx, Math.Max(0.25, 1.0 / Math.Max(1e-9, file.NmPerPixel)), file.NmPerPixel);
            var smooth = StatisticsAndGeometry.MovingGaussianSmooth(rawProfile.Values, 2);
            var corrected = BaselineCorrect(smooth);
            for (var k = 0; k < corrected.Length; k++)
            {
                roughnessAccumulator += Math.Abs(rawProfile.Values[k] - smooth[k]);
                roughnessCount++;
            }

            var peaks = FindPeaks(corrected, rawProfile.OffsetsNm);
            var best = peaks.OrderByDescending(p => p.Height).FirstOrDefault();
            if (best is null)
            {
                metrics.Add(new GuidedMetric { ArcNm = sampled[i].ArcNm, WidthNm = 0, HeightNm = 0, Valid = false });
                continue;
            }

            var separation = ComputePeakSeparationWidthNm(peaks);
            var width = ComputeMorphologyWidthNm(peaks, corrected, rawProfile.OffsetsNm, best);
            if (peaks.Count >= 2)
            {
                var ordered = peaks.OrderBy(p => p.OffsetNm).Take(2).ToArray();
                leftPeaks.Add(ordered[0].Height);
                rightPeaks.Add(ordered[1].Height);
            }
            if (separation > 1e-9) separations.Add(separation);
            widths.Add(width);
            heights.Add(best.Height);
            rawHeights.Add(Math.Max(0, smooth[best.Index]));
            ratios.Add(best.Height / Math.Max(1e-9, width));
            prominences.Add(best.Height);
            metrics.Add(new GuidedMetric { ArcNm = sampled[i].ArcNm, WidthNm = width, HeightNm = best.Height, Valid = true });
        }

        file.GuidedMetrics.Clear();
        foreach (var metric in metrics) file.GuidedMetrics.Add(metric);

        var widthSummary = StatisticsAndGeometry.Summarise(widths);
        var heightSummary = StatisticsAndGeometry.Summarise(heights);
        var rawHeightSummary = StatisticsAndGeometry.Summarise(rawHeights);
        var ratioSummary = StatisticsAndGeometry.Summarise(ratios);
        if (widthSummary is null || heightSummary is null) return null;

        var meanProm = StatisticsAndGeometry.Mean(prominences);
        var peakSeparation = separations.Count == 0 ? 0 : StatisticsAndGeometry.Mean(separations);
        var dipDepth = EstimateDipDepth(file, sampled, corridorHalfWidthPx);
        return new GuidedSummary
        {
            ProfileCount = metrics.Count,
            ValidProfileCount = metrics.Count(m => m.Valid),
            Continuity = metrics.Count == 0 ? 0 : metrics.Count(m => m.Valid) / (double)metrics.Count,
            GuideLengthNm = StatisticsAndGeometry.PolylineLengthPixels(file.GuidePoints) * file.NmPerPixel,
            MeanWidthNm = widthSummary.Mean,
            MeanHeightNm = heightSummary.Mean,
            RawMeanHeightNm = rawHeightSummary?.Mean ?? heightSummary.Mean,
            WidthStdNm = widthSummary.StandardDeviation,
            HeightStdNm = heightSummary.StandardDeviation,
            RawHeightStdNm = rawHeightSummary?.StandardDeviation ?? heightSummary.StandardDeviation,
            WidthSemNm = widthSummary.StandardError,
            HeightSemNm = heightSummary.StandardError,
            RawHeightSemNm = rawHeightSummary?.StandardError ?? heightSummary.StandardError,
            RoughnessNm = roughnessCount == 0 ? 0 : roughnessAccumulator / roughnessCount,
            CurvatureMean = curvatures.Count == 0 ? 0 : StatisticsAndGeometry.Mean(curvatures),
            PeakSeparationNm = peakSeparation,
            DipDepthNm = dipDepth,
            BimodalWeight = peakSeparation > 0 ? 1 : 0,
            HeightToWidthRatio = ratioSummary?.Mean ?? heightSummary.Mean / Math.Max(1e-9, widthSummary.Mean),
            WidthSummary = widthSummary,
            HeightSummary = heightSummary,
            RawHeightSummary = rawHeightSummary,
            HeightWidthRatioSummary = ratioSummary
        };
    }

    public string ClassifyStage(double[] data)
    {
        if (data.Length == 0) return "early";
        var min = data.Min();
        var max = data.Max();
        var prominence = max - min;
        var range = Math.Max(1e-9, max - min);
        var normalized = prominence / range;
        if (normalized < 0.15) return "early";
        if (normalized < 0.35) return "middle";
        return "late";
    }

    public EvolutionRecord BuildEvolutionRecord(PiecrustFileState file)
    {
        var row = Math.Clamp(file.PixelHeight / 2, 0, Math.Max(0, file.PixelHeight - 1));
        var values = new double[file.PixelWidth];
        for (var x = 0; x < file.PixelWidth; x++) values[x] = file.HeightData[row * file.PixelWidth + x];
        var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(values, 3);
        var shifted = ShiftToZero(BaselineCorrect(smoothed));
        return new EvolutionRecord
        {
            FileName = file.Name,
            Stage = file.Stage,
            Profile = Resample(shifted, 160),
            GaussianParameters = ExtractDoubleGaussian(shifted)
        };
    }

    public Dictionary<string, EvolutionStageBucket> BuildEvolutionBuckets(IEnumerable<PiecrustFileState> files)
    {
        var output = new Dictionary<string, EvolutionStageBucket>
        {
            ["early"] = new() { Stage = "early" },
            ["middle"] = new() { Stage = "middle" },
            ["late"] = new() { Stage = "late" }
        };

        foreach (var file in files)
        {
            if (file.EvolutionRecord is null) continue;
            output[file.Stage].Records.Add(file.EvolutionRecord);
        }

        foreach (var bucket in output.Values)
        {
            bucket.MeanProfile = AverageVectors(bucket.Records.Select(r => r.Profile));
            bucket.MeanParameters = AverageVectors(bucket.Records.Select(r => r.GaussianParameters));
        }
        return output;
    }

    public double[]? BuildCenteredStageOverlayProfile(EvolutionStageBucket bucket, int fallbackCount = 160)
    {
        if (bucket.MeanParameters is { Length: >= 6 })
        {
            var template = BuildCenteredGaussianTemplate(bucket.MeanParameters, fallbackCount);
            if (template.Length > 0) return template;
        }

        return bucket.MeanProfile is { Length: > 0 }
            ? Resample(bucket.MeanProfile, fallbackCount)
            : null;
    }

    public IReadOnlyList<PlotPoint> PredictEvolutionProfile(Dictionary<string, EvolutionStageBucket> buckets, double progress)
    {
        var profiles = new Dictionary<string, double[]?>
        {
            ["early"] = BuildCenteredStageOverlayProfile(buckets["early"]),
            ["middle"] = BuildCenteredStageOverlayProfile(buckets["middle"]),
            ["late"] = BuildCenteredStageOverlayProfile(buckets["late"])
        };

        var vector = InterpolateStageVectors(profiles, progress);
        if (vector is null) return Array.Empty<PlotPoint>();
        return vector.Select((v, i) => new PlotPoint(i * 100.0 / Math.Max(1, vector.Length - 1), Math.Max(0, v))).ToArray();
    }

    public IReadOnlyList<BoxPlotDataset> BuildHeightBoxPlots(IEnumerable<PiecrustFileState> files)
    {
        var output = new List<BoxPlotDataset>();
        foreach (var file in OrderFilesForPerImageBoxPlots(files))
        {
            var summary = file.GuidedSummary;
            var stats = summary?.RawHeightSummary ?? summary?.HeightSummary;
            if (stats is null || stats.Count == 0) continue;

            output.Add(new BoxPlotDataset
            {
                Label = file.SequenceOrder.ToString(),
                FileName = file.Name,
                Stage = file.Stage,
                SequenceOrder = file.SequenceOrder,
                Stats = stats,
                Color = GetStageColor(file.Stage, "#c17832"),
                MeanMarker = summary!.RawMeanHeightNm,
                MeanError = summary.RawHeightSemNm
            });
        }
        return output;
    }

    public IReadOnlyList<BoxPlotDataset> BuildWidthBoxPlots(IEnumerable<PiecrustFileState> files)
    {
        var output = new List<BoxPlotDataset>();
        foreach (var file in OrderFilesForPerImageBoxPlots(files))
        {
            var summary = file.GuidedSummary;
            var stats = summary?.WidthSummary;
            if (stats is null || stats.Count == 0) continue;

            output.Add(new BoxPlotDataset
            {
                Label = file.SequenceOrder.ToString(),
                FileName = file.Name,
                Stage = file.Stage,
                SequenceOrder = file.SequenceOrder,
                Stats = stats,
                Color = GetStageColor(file.Stage, "#c17832"),
                MeanMarker = summary!.MeanWidthNm,
                MeanError = summary.WidthSemNm
            });
        }
        return output;
    }

    public IReadOnlyList<BoxPlotDataset> BuildHeightWidthRatioBoxPlots(IEnumerable<PiecrustFileState> files)
    {
        var output = new List<BoxPlotDataset>();
        foreach (var file in OrderFilesForPerImageBoxPlots(files))
        {
            var summary = file.GuidedSummary;
            var stats = summary?.HeightWidthRatioSummary
                ?? StatisticsAndGeometry.Summarise(file.GuidedMetrics
                    .Where(metric => metric.Valid && metric.WidthNm > 1e-9)
                    .Select(metric => metric.HeightNm / Math.Max(1e-9, metric.WidthNm))
                    .Where(double.IsFinite)
                    .ToArray());
            if (stats is null || stats.Count == 0) continue;

            output.Add(new BoxPlotDataset
            {
                Label = file.SequenceOrder.ToString(),
                FileName = file.Name,
                Stage = file.Stage,
                SequenceOrder = file.SequenceOrder,
                Stats = stats,
                Color = GetStageColor(file.Stage, "#c17832"),
                MeanMarker = summary!.HeightToWidthRatio,
                MeanError = stats.StandardError
            });
        }
        return output;
    }

    public IReadOnlyList<StageSummaryRow> BuildStageSummaries(IEnumerable<PiecrustFileState> files)
    {
        var rows = new List<StageSummaryRow>();
        foreach (var stage in StageOrder)
        {
            var stageFiles = files
                .Where(file => string.Equals(file.Stage, stage, StringComparison.OrdinalIgnoreCase) && file.GuidedSummary is not null)
                .ToArray();
            if (stageFiles.Length == 0) continue;

            // Stage-level height summaries should follow the same raw-height definition used by the
            // line-profile display and the height box plots, otherwise the summary can look artificially low.
            var heightMeans = stageFiles.Select(file => file.GuidedSummary!.RawMeanHeightNm).Where(double.IsFinite).ToArray();
            var widthMeans = stageFiles.Select(file => file.GuidedSummary!.MeanWidthNm).Where(double.IsFinite).ToArray();
            var ratioMeans = stageFiles
                .Select(file =>
                {
                    var summary = file.GuidedSummary!;
                    return summary.RawMeanHeightNm / Math.Max(1e-9, summary.MeanWidthNm);
                })
                .Where(double.IsFinite)
                .ToArray();
            rows.Add(new StageSummaryRow
            {
                Stage = stage,
                ImageCount = stageFiles.Length,
                HeightMeanNm = heightMeans.Length == 0 ? 0 : StatisticsAndGeometry.Mean(heightMeans),
                HeightStdNm = heightMeans.Length == 0 ? 0 : StatisticsAndGeometry.StandardDeviation(heightMeans),
                WidthMeanNm = widthMeans.Length == 0 ? 0 : StatisticsAndGeometry.Mean(widthMeans),
                WidthStdNm = widthMeans.Length == 0 ? 0 : StatisticsAndGeometry.StandardDeviation(widthMeans),
                HeightWidthRatioMean = ratioMeans.Length == 0 ? 0 : StatisticsAndGeometry.Mean(ratioMeans),
                HeightWidthRatioStd = ratioMeans.Length == 0 ? 0 : StatisticsAndGeometry.StandardDeviation(ratioMeans)
            });
        }

        return rows;
    }

    public GrowthQuantificationRow? BuildGrowthQuantification(IReadOnlyList<PiecrustFileState> files, PiecrustFileState file)
    {
        if (file.GuidedSummary is null) return null;
        var addition = Math.Max(0, file.GuidedSummary.MeanHeightNm);
        var removal = ComputeRemovalSignal(file.GuidedSummary);
        var rawCompromise = removal / Math.Max(1e-9, addition + removal);
        var controls = SelectControlReferenceFiles(files, file);

        double compromise = rawCompromise;
        double profileDeviation = 0;
        if (controls.Length > 0)
        {
            var controlAdditions = controls
                .Select(control => Math.Max(0, control.GuidedSummary!.MeanHeightNm))
                .ToArray();
            var controlRemovals = controls
                .Select(control => ComputeRemovalSignal(control.GuidedSummary!))
                .ToArray();
            var controlRawCompromises = controlRemovals
                .Select((controlRemoval, index) => controlRemoval / Math.Max(1e-9, controlAdditions[index] + controlRemoval))
                .ToArray();

            var controlAdditionMean = StatisticsAndGeometry.Mean(controlAdditions);
            var controlRemovalMean = StatisticsAndGeometry.Mean(controlRemovals);
            var controlRawMean = StatisticsAndGeometry.Mean(controlRawCompromises);

            var heightLoss = Math.Max(0, controlAdditionMean - addition) / Math.Max(1e-9, controlAdditionMean);
            var removalGain = Math.Max(0, removal - controlRemovalMean) / Math.Max(1e-9, controlRemovalMean);
            var rawDelta = Math.Max(0, rawCompromise - controlRawMean) / Math.Max(1e-9, 1.0 - controlRawMean);
            profileDeviation = ComputeControlProfileDeviation01(file, controls);

            compromise = StatisticsAndGeometry.Clamp(
                0.35 * rawDelta +
                0.25 * heightLoss +
                0.20 * removalGain +
                0.20 * profileDeviation,
                0,
                1);
        }

        return new GrowthQuantificationRow
        {
            FileName = file.Name,
            Stage = file.Stage,
            ConditionType = file.ConditionType,
            DoseUgPerMl = file.ConditionType == "control" ? 0 : file.AntibioticDoseUgPerMl,
            AdditionRateNm = addition,
            RemovalRateNm = removal,
            RawCompromiseRatio = rawCompromise,
            CompromiseRatio = compromise,
            ControlProfileDeviation = profileDeviation,
            ControlReferenceCount = controls.Length,
            CompromiseDisplayText = controls.Length > 0
                ? $"Compromise vs control {compromise:F3}"
                : $"Raw compromise {compromise:F3}",
            MeanHeightNm = file.GuidedSummary.MeanHeightNm,
            MeanWidthNm = file.GuidedSummary.MeanWidthNm,
            HeightSemNm = file.GuidedSummary.HeightSemNm,
            WidthSemNm = file.GuidedSummary.WidthSemNm,
            HeightToWidthRatio = file.GuidedSummary.HeightToWidthRatio
        };
    }

    public SurfaceSimulationResult? BuildSurfaceSimulation(IReadOnlyList<PiecrustFileState> files, PiecrustFileState startFile, PiecrustFileState endFile, SupervisedGrowthModel? supervisedModel = null, string constraintMode = "current", int frameCount = 21, int maxGridWidth = 180)
    {
        if (files.Count == 0 || ReferenceEquals(startFile, endFile)) return null;
        var ordered = BuildOrderedSimulationReferences(files, startFile, endFile);
        if (ordered.Files.Length < 2) return null;

        frameCount = Math.Max(3, frameCount);
        var useGuidedAlignment = ordered.Files.All(HasUsableGuide);
        var targetWidthNm = useGuidedAlignment
            ? Math.Max(1, ordered.Files.Max(file => Math.Max(4, file.GuideCorridorWidthNm * 1.2)))
            : GetScanSizeXNm(startFile);
        var targetHeightNm = useGuidedAlignment
            ? Math.Max(1, ordered.Files.Max(file => Math.Max(file.NmPerPixel, StatisticsAndGeometry.PolylineLengthPixels(file.GuidePoints) * file.NmPerPixel)))
            : GetScanSizeYNm(startFile);

        var pixelsPerNm = Math.Min(maxGridWidth / Math.Max(1, targetWidthNm), maxGridWidth / Math.Max(1, targetHeightNm));
        var targetWidth = Math.Clamp((int)Math.Round(targetWidthNm * pixelsPerNm), 96, maxGridWidth);
        var targetHeight = Math.Clamp((int)Math.Round(targetHeightNm * pixelsPerNm), 96, maxGridWidth);

        var surfaces = new List<double[]>(ordered.Files.Length);
        double[]? lockedReferenceSurface = null;

        for (var fileIndex = 0; fileIndex < ordered.Files.Length; fileIndex++)
        {
            var file = ordered.Files[fileIndex];
            double[] resampled;
            if (useGuidedAlignment)
            {
                resampled = ResampleGuidedCorridorSurface(file, targetWidthNm, targetHeightNm, targetWidth, targetHeight);
            }
            else
            {
                resampled = ResampleSurface(file.HeightData, file.PixelWidth, file.PixelHeight, targetWidth, targetHeight);
                if (resampled.Length == 0) return null;
            }

            var prepared = PrepareSimulationSurface(resampled, targetWidth, targetHeight);
            var centered = CenterSurfaceByBimodalProfile(prepared, targetWidth, targetHeight);
            if (lockedReferenceSurface is null)
            {
                lockedReferenceSurface = centered;
            }
            else
            {
                centered = AlignSurfaceToReference(centered, lockedReferenceSurface, targetWidth, targetHeight);
            }

            surfaces.Add(centered);
        }

        var degree = Math.Min(3, ordered.Files.Length - 1);
        if (degree < 1) return null;
        var scanSizeNmX = useGuidedAlignment ? targetWidthNm : GetScanSizeXNm(startFile);
        var bimodalTrajectory = BuildSimulationBimodalTrajectory(surfaces, ordered.Positions, targetWidth, targetHeight, scanSizeNmX, degree, supervisedModel is { ExampleCount: >= 3 }, constraintMode);

        var projector = BuildPolynomialProjector(ordered.Positions, degree);
        var pixelCount = targetWidth * targetHeight;
        var frames = new double[frameCount][];
        var frameProgresses = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            frames[i] = new double[pixelCount];
            frameProgresses[i] = i / (double)(frameCount - 1);
        }

        var values = new double[ordered.Files.Length];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            for (var referenceIndex = 0; referenceIndex < surfaces.Count; referenceIndex++)
            {
                values[referenceIndex] = surfaces[referenceIndex][pixelIndex];
            }

            var coefficients = Multiply(projector, values);
            var min = values.Min();
            var max = values.Max();
            var span = Math.Max(1e-6, max - min);
            var lowClamp = Math.Max(0, min - span * 0.12);
            var highClamp = max + span * 0.12;

            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var predicted = EvaluatePolynomial(coefficients, frameProgresses[frameIndex]);
                frames[frameIndex][pixelIndex] = StatisticsAndGeometry.Clamp(predicted, lowClamp, highClamp);
            }
        }

        var usesSupervisedLearning = supervisedModel is { ExampleCount: >= 3 } && lockedReferenceSurface is not null;
        var supervisedBlendWeight = usesSupervisedLearning ? supervisedModel!.BlendWeight : 0;
        if (usesSupervisedLearning)
        {
            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var context = BuildGrowthPredictionContext(ordered.Files, ordered.Positions, frameProgresses[frameIndex], frames[frameIndex], targetWidth, targetHeight, targetWidthNm);
                var predictedShape = SupervisedGrowthLearningService.PredictShape(supervisedModel!, context);
                if (predictedShape.Length != 5) continue;

                var predictedProfile = BuildCenteredGaussianTemplateFromShape(predictedShape, targetWidth);
                if (predictedProfile.Length != targetWidth) continue;

                var guidedFrame = ApplySupervisedProfileGuidance(frames[frameIndex], targetWidth, targetHeight, predictedProfile, supervisedBlendWeight);
                guidedFrame = CenterSurfaceByBimodalProfile(guidedFrame, targetWidth, targetHeight);
                frames[frameIndex] = AlignSurfaceToReference(guidedFrame, lockedReferenceSurface!, targetWidth, targetHeight, maxShift: 6);
            }
        }

        if (bimodalTrajectory is not null && lockedReferenceSurface is not null)
        {
            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var predictedParameters = EvaluateSimulationBimodalParameters(bimodalTrajectory, frameProgresses[frameIndex]);
                if (predictedParameters.Length < 5) continue;

                var predictedProfile = BuildCenteredBimodalProfileFromParameters(predictedParameters, targetWidth, scanSizeNmX)
                    .Select(point => point.Y)
                    .ToArray();
                if (predictedProfile.Length != targetWidth) continue;

                var guidedFrame = ApplyBimodalTrajectoryGuidance(frames[frameIndex], targetWidth, targetHeight, predictedProfile, bimodalTrajectory.GuidanceBlendWeight);
                guidedFrame = CenterSurfaceByBimodalProfile(guidedFrame, targetWidth, targetHeight);
                frames[frameIndex] = AlignSurfaceToReference(guidedFrame, lockedReferenceSurface, targetWidth, targetHeight, maxShift: 4);
            }
        }

        var displayRange = EstimateSurfaceRange(frames);
        var scanSizeNmY = useGuidedAlignment ? targetHeightNm : GetScanSizeYNm(startFile);

        return new SurfaceSimulationResult
        {
            ConstraintMode = string.IsNullOrWhiteSpace(constraintMode) ? "current" : constraintMode,
            Width = targetWidth,
            Height = targetHeight,
            ScanSizeNmX = scanSizeNmX,
            ScanSizeNmY = scanSizeNmY,
            Unit = startFile.Unit,
            PolynomialDegree = degree,
            References = ordered.Files.Select((file, index) => new SimulationReferenceInfo
            {
                FileName = file.Name,
                Stage = file.Stage,
                SequenceOrder = file.SequenceOrder,
                Position01 = ordered.Positions[index]
            }).ToArray(),
            ReferenceSurfaces = surfaces,
            FrameProgresses = frameProgresses,
            Frames = frames,
            DisplayMin = displayRange.Min,
            DisplayMax = displayRange.Max,
            UsesGuidedAlignment = useGuidedAlignment,
            UsesSupervisedLearning = usesSupervisedLearning,
            SupervisedExampleCount = supervisedModel?.ExampleCount ?? 0,
            SupervisedBlendWeight = supervisedBlendWeight,
            BimodalTrajectory = bimodalTrajectory
        };
    }

    public double[] BuildInterpolatedSimulationFrame(SurfaceSimulationResult simulation, double progress)
    {
        if (simulation.Frames.Count == 0) return Array.Empty<double>();
        if (simulation.Frames.Count == 1) return simulation.Frames[0];
        progress = StatisticsAndGeometry.Clamp(progress, 0, 1);
        var scaled = progress * (simulation.Frames.Count - 1);
        var lo = (int)Math.Floor(scaled);
        var hi = Math.Min(simulation.Frames.Count - 1, lo + 1);
        var mix = scaled - lo;
        if (hi == lo || mix <= 1e-6) return simulation.Frames[lo];

        var a = simulation.Frames[lo];
        var b = simulation.Frames[hi];
        var output = new double[a.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = a[i] * (1 - mix) + b[i] * mix;
        }
        return output;
    }

    public IReadOnlyList<PlotPoint> BuildBimodalPolynomialSimulationProfile(SurfaceSimulationResult simulation, double progress)
    {
        if (simulation.BimodalTrajectory is null) return Array.Empty<PlotPoint>();
        var parameters = EvaluateSimulationBimodalParameters(simulation.BimodalTrajectory, progress);
        return parameters.Length < 5
            ? Array.Empty<PlotPoint>()
            : BuildCenteredBimodalProfileFromParameters(parameters, simulation.Width, simulation.ScanSizeNmX);
    }

    public double[] EvaluateSimulationBimodalParameters(SimulationBimodalTrajectory trajectory, double progress)
    {
        progress = StatisticsAndGeometry.Clamp(progress, 0, 1);
        return
        [
            Math.Max(0, EvaluatePolynomialCurve(trajectory.LeftAmplitudeCoefficients, progress)),
            Math.Max(1e-3, Math.Abs(EvaluatePolynomialCurve(trajectory.LeftSigmaCoefficients, progress))),
            Math.Max(0, EvaluatePolynomialCurve(trajectory.RightAmplitudeCoefficients, progress)),
            Math.Max(1e-3, Math.Abs(EvaluatePolynomialCurve(trajectory.RightSigmaCoefficients, progress))),
            Math.Max(0, Math.Abs(EvaluatePolynomialCurve(trajectory.SeparationCoefficients, progress)))
        ];
    }

    public IReadOnlyList<PlotPoint> BuildSurfaceCrossSection(double[] frame, int width, int height, double scanSizeNmX)
    {
        if (frame.Length == 0 || width <= 0 || height <= 0) return Array.Empty<PlotPoint>();
        var rowCenter = Math.Clamp(height / 2, 0, Math.Max(0, height - 1));
        var rowStart = Math.Max(0, rowCenter - 2);
        var rowEnd = Math.Min(height - 1, rowCenter + 2);
        var values = new double[width];
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            var count = 0;
            for (var y = rowStart; y <= rowEnd; y++)
            {
                sum += frame[y * width + x];
                count++;
            }

            values[x] = Math.Max(0, sum / Math.Max(1, count));
        }

        var corrected = ShiftToZero(BaselineCorrect(values));
        var points = new PlotPoint[width];
        for (var x = 0; x < width; x++)
        {
            var xNm = GetCenteredAxisPositionNm(x, width, scanSizeNmX);
            points[x] = new PlotPoint(xNm, corrected[x]);
        }

        return points;
    }

    public IReadOnlyList<PlotPoint> BuildCenteredBimodalSimulationProfile(double[] frame, int width, int height, double scanSizeNmX)
    {
        var rawProfile = BuildSurfaceCrossSection(frame, width, height, scanSizeNmX);
        if (rawProfile.Count == 0) return Array.Empty<PlotPoint>();

        var values = rawProfile.Select(point => point.Y).ToArray();
        if (values.Length < 8) return rawProfile;

        var xOffsets = rawProfile.Select(point => point.X).ToArray();
        var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(values, 2);
        var fitted = BuildLocalPolynomialProfileFit(smoothed);
        fitted = PreserveDetectedTramline(smoothed, fitted, xOffsets);
        if (fitted.Length != rawProfile.Count) return rawProfile;

        return fitted
            .Select((value, index) => new PlotPoint(rawProfile[index].X, Math.Max(0, value)))
            .ToArray();
    }

    private static double[] BuildLocalPolynomialProfileFit(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var broadWindow = ChooseAdaptiveSavitzkyGolayWindow(values.Count, 0.18, 29, 9);
        var detailWindow = ChooseAdaptiveSavitzkyGolayWindow(values.Count, 0.10, 15, 5);
        var fitted = StatisticsAndGeometry.SavitzkyGolaySmooth(values, broadWindow, 3);
        return StatisticsAndGeometry.SavitzkyGolaySmooth(fitted, detailWindow, 3);
    }

    private static int ChooseAdaptiveSavitzkyGolayWindow(int count, double fraction, int maxWindow, int minWindow)
    {
        if (count < 3) return count;
        var window = (int)Math.Round(count * fraction);
        window = Math.Max(minWindow, Math.Min(maxWindow, window));
        if (window >= count) window = count - 1;
        if (window < 3) window = Math.Min(count, 3);
        if (window % 2 == 0) window--;
        if (window < 3) window = Math.Min(count | 1, count);
        return Math.Max(3, Math.Min(count % 2 == 1 ? count : count - 1, window));
    }

    private static double[] PreserveDetectedTramline(IReadOnlyList<double> rawProfile, IReadOnlyList<double> fittedProfile, IReadOnlyList<double> offsetsNm)
    {
        if (rawProfile.Count == 0 || rawProfile.Count != fittedProfile.Count || rawProfile.Count != offsetsNm.Count)
        {
            return fittedProfile.ToArray();
        }

        var blended = fittedProfile.ToArray();
        var correctedRaw = ShiftToZero(BaselineCorrect(StatisticsAndGeometry.MovingGaussianSmooth(rawProfile, 2)));
        var dominantPeaks = FindDominantTramlinePeaks(correctedRaw, offsetsNm);
        if (dominantPeaks.Length < 2)
        {
            for (var i = 0; i < blended.Length; i++)
            {
                blended[i] = fittedProfile[i] * 0.84 + rawProfile[i] * 0.16;
            }

            return blended;
        }

        var leftPeak = dominantPeaks[0];
        var rightPeak = dominantPeaks[1];
        var valleyIndex = FindValleyIndex(correctedRaw, leftPeak.Index, rightPeak.Index);
        var valleyOffsetNm = offsetsNm[valleyIndex];
        var separationNm = Math.Max(1e-6, Math.Abs(rightPeak.OffsetNm - leftPeak.OffsetNm));
        var totalSpanNm = Math.Max(1e-6, offsetsNm[^1] - offsetsNm[0]);
        var featureSigmaNm = Math.Max(totalSpanNm * 0.035, separationNm * 0.22);
        var valleySigmaNm = Math.Max(totalSpanNm * 0.030, featureSigmaNm * 0.85);

        for (var i = 0; i < blended.Length; i++)
        {
            var xNm = offsetsNm[i];
            var leftWeight = Math.Exp(-Math.Pow(xNm - leftPeak.OffsetNm, 2) / (2 * featureSigmaNm * featureSigmaNm));
            var rightWeight = Math.Exp(-Math.Pow(xNm - rightPeak.OffsetNm, 2) / (2 * featureSigmaNm * featureSigmaNm));
            var valleyWeight = Math.Exp(-Math.Pow(xNm - valleyOffsetNm, 2) / (2 * valleySigmaNm * valleySigmaNm));
            var structureWeight = StatisticsAndGeometry.Clamp(Math.Max(Math.Max(leftWeight, rightWeight), valleyWeight * 0.9), 0, 1);
            var blendWeight = 0.18 + structureWeight * 0.55;
            blended[i] = fittedProfile[i] * (1 - blendWeight) + rawProfile[i] * blendWeight;
        }

        var refinementWindow = ChooseAdaptiveSavitzkyGolayWindow(blended.Length, 0.08, 11, 5);
        return StatisticsAndGeometry.SavitzkyGolaySmooth(blended, refinementWindow, 2);
    }

    private static PeakInfo[] FindDominantTramlinePeaks(IReadOnlyList<double> values, IReadOnlyList<double> offsetsNm)
    {
        var peaks = FindPeaks(values, offsetsNm);
        if (peaks.Count < 2) return Array.Empty<PeakInfo>();

        var centerOffset = offsetsNm.Count == 0 ? 0 : (offsetsNm[0] + offsetsNm[^1]) / 2.0;
        var left = peaks
            .Where(peak => peak.OffsetNm <= centerOffset)
            .OrderByDescending(peak => peak.Height)
            .FirstOrDefault();
        var right = peaks
            .Where(peak => peak.OffsetNm >= centerOffset)
            .OrderByDescending(peak => peak.Height)
            .FirstOrDefault();

        if (left is not null && right is not null && left.Index != right.Index)
        {
            return new[] { left, right }.OrderBy(peak => peak.OffsetNm).ToArray();
        }

        return peaks
            .Take(2)
            .OrderBy(peak => peak.OffsetNm)
            .ToArray();
    }

    private static int FindValleyIndex(IReadOnlyList<double> values, int leftIndex, int rightIndex)
    {
        if (values.Count == 0) return 0;
        leftIndex = Math.Clamp(leftIndex, 0, values.Count - 1);
        rightIndex = Math.Clamp(rightIndex, leftIndex, values.Count - 1);
        var valleyIndex = leftIndex;
        var valleyValue = values[leftIndex];
        for (var i = leftIndex + 1; i <= rightIndex; i++)
        {
            if (values[i] >= valleyValue) continue;
            valleyValue = values[i];
            valleyIndex = i;
        }

        return valleyIndex;
    }

    public double[] ExtractCenteredBimodalSimulationParameters(double[] frame, int width, int height, double scanSizeNmX)
    {
        var rawProfile = BuildSurfaceCrossSection(frame, width, height, scanSizeNmX);
        if (rawProfile.Count == 0) return Array.Empty<double>();

        var values = rawProfile.Select(point => point.Y).ToArray();
        var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(values, 3);
        var corrected = ShiftToZero(BaselineCorrect(smoothed));
        if (corrected.Length < 8) return Array.Empty<double>();

        var parameters = ExtractDoubleGaussian(corrected);
        if (parameters.Length < 6) return Array.Empty<double>();
        var spacingNm = rawProfile.Count > 1 ? (rawProfile[^1].X - rawProfile[0].X) / (rawProfile.Count - 1.0) : 1.0;
        return
        [
            Math.Max(0, parameters[0]),
            Math.Max(0.5 * spacingNm, Math.Abs(parameters[2]) * spacingNm),
            Math.Max(0, parameters[3]),
            Math.Max(0.5 * spacingNm, Math.Abs(parameters[5]) * spacingNm),
            Math.Max(0, Math.Abs(parameters[4] - parameters[1]) * spacingNm)
        ];
    }

    public IReadOnlyList<PlotPoint> BuildCenteredBimodalProfileFromParameters(IReadOnlyList<double> parameters, int count, double scanSizeNmX)
    {
        if (parameters.Count < 5 || count <= 0) return Array.Empty<PlotPoint>();
        var amplitudeLeft = Math.Max(0, parameters[0]);
        var sigmaLeftNm = Math.Max(1e-6, Math.Abs(parameters[1]));
        var amplitudeRight = Math.Max(0, parameters[2]);
        var sigmaRightNm = Math.Max(1e-6, Math.Abs(parameters[3]));
        var separationNm = Math.Max(0, Math.Abs(parameters[4]));
        var leftCenterNm = -separationNm / 2.0;
        var rightCenterNm = separationNm / 2.0;
        var points = new PlotPoint[count];
        for (var i = 0; i < count; i++)
        {
            var sNm = GetCenteredAxisPositionNm(i, count, scanSizeNmX);
            var left = amplitudeLeft * Math.Exp(-Math.Pow(sNm - leftCenterNm, 2) / (2 * sigmaLeftNm * sigmaLeftNm));
            var right = amplitudeRight * Math.Exp(-Math.Pow(sNm - rightCenterNm, 2) / (2 * sigmaRightNm * sigmaRightNm));
            points[i] = new PlotPoint(sNm, Math.Max(0, left + right));
        }

        return points;
    }

    private SimulationBimodalTrajectory? BuildSimulationBimodalTrajectory(
        IReadOnlyList<double[]> referenceSurfaces,
        IReadOnlyList<double> positions,
        int width,
        int height,
        double scanSizeNmX,
        int degree,
        bool usesSupervisedLearning,
        string constraintMode)
    {
        if (referenceSurfaces.Count == 0 || referenceSurfaces.Count != positions.Count) return null;

        var samples = new List<(double Tau, double[] Parameters)>(referenceSurfaces.Count);
        for (var i = 0; i < referenceSurfaces.Count; i++)
        {
            var parameters = ExtractCenteredBimodalSimulationParameters(referenceSurfaces[i], width, height, scanSizeNmX);
            if (parameters.Length < 5) continue;
            samples.Add((StatisticsAndGeometry.Clamp(positions[i], 0, 1), parameters));
        }

        if (samples.Count < 2) return null;
        degree = Math.Clamp(degree, 1, Math.Max(1, samples.Count - 1));

        var taus = samples.Select(sample => sample.Tau).ToArray();
        var normalizedConstraintMode = string.IsNullOrWhiteSpace(constraintMode) ? "current" : constraintMode;
        var leftAmplitudeValues = samples.Select(sample => sample.Parameters[0]).ToArray();
        var leftSigmaValues = samples.Select(sample => sample.Parameters[1]).ToArray();
        var rightAmplitudeValues = samples.Select(sample => sample.Parameters[2]).ToArray();
        var rightSigmaValues = samples.Select(sample => sample.Parameters[3]).ToArray();
        var separationValues = samples.Select(sample => sample.Parameters[4]).ToArray();

        var leftAmplitude = FitPolynomialCurve(taus, leftAmplitudeValues, degree);
        var rightAmplitude = FitPolynomialCurve(taus, rightAmplitudeValues, degree);
        var leftSigma = normalizedConstraintMode is "constant_peak_width" or "amplitude_only"
            ? BuildConstantCurve(leftSigmaValues)
            : FitPolynomialCurve(taus, leftSigmaValues, degree);
        var rightSigma = normalizedConstraintMode is "constant_peak_width" or "amplitude_only"
            ? BuildConstantCurve(rightSigmaValues)
            : FitPolynomialCurve(taus, rightSigmaValues, degree);
        var separation = normalizedConstraintMode is "constant_separation" or "amplitude_only"
            ? BuildConstantCurve(separationValues)
            : FitPolynomialCurve(taus, separationValues, degree);
        if (leftAmplitude.Length == 0 || leftSigma.Length == 0 || rightAmplitude.Length == 0 || rightSigma.Length == 0 || separation.Length == 0)
        {
            return null;
        }

        return new SimulationBimodalTrajectory
        {
            ConstraintMode = normalizedConstraintMode,
            Degree = degree,
            LeftAmplitudeCoefficients = leftAmplitude,
            LeftSigmaCoefficients = leftSigma,
            RightAmplitudeCoefficients = rightAmplitude,
            RightSigmaCoefficients = rightSigma,
            SeparationCoefficients = separation,
            GuidanceBlendWeight = usesSupervisedLearning ? 0.28 : 0.42
        };
    }

    private static double[] BuildConstantCurve(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        return [StatisticsAndGeometry.Mean(values.ToArray())];
    }

    public double[] FitPolynomialCurve(IReadOnlyList<double> positions, IReadOnlyList<double> values, int degree)
    {
        if (positions.Count == 0 || values.Count == 0 || positions.Count != values.Count) return Array.Empty<double>();
        degree = Math.Clamp(degree, 1, Math.Max(1, positions.Count - 1));
        var projector = BuildPolynomialProjector(positions, degree);
        return Multiply(projector, values);
    }

    public double EvaluatePolynomialCurve(IReadOnlyList<double> coefficients, double position)
    {
        return EvaluatePolynomial(coefficients, position);
    }

    public double[] BuildSimulationProfile(PiecrustFileState startFile, PiecrustFileState endFile, double progress)
    {
        var start = GetReferenceProfile(startFile);
        var end = GetReferenceProfile(endFile);
        if (start.Length == 0 || end.Length == 0) return Array.Empty<double>();
        if (start.Length != end.Length)
        {
            var n = Math.Max(start.Length, end.Length);
            start = Resample(start, n);
            end = Resample(end, n);
        }

        var eased = SmoothStep(StatisticsAndGeometry.Clamp(progress, 0, 1));
        var output = new double[start.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = Math.Max(0, start[i] * (1 - eased) + end[i] * eased);
        }
        return output;
    }

    public IReadOnlyList<double[]> BuildSimulationFrames(PiecrustFileState startFile, PiecrustFileState endFile, int frameCount)
    {
        frameCount = Math.Max(2, frameCount);
        var frames = new List<double[]>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            var t = i / (double)(frameCount - 1);
            frames.Add(BuildSimulationProfile(startFile, endFile, t));
        }
        return frames;
    }

    private static (double[] Values, double[] OffsetsNm) GetPerpendicularProfile(double[] data, int width, int height, PointD centrePoint, PointD tangent, double profileHalfLengthPx, double sampleStepPx, double nmPerPixel)
    {
        var normal = new PointD(-tangent.Y, tangent.X);
        var steps = Math.Max(8, (int)Math.Round((profileHalfLengthPx * 2) / Math.Max(0.25, sampleStepPx)));
        var values = new double[steps + 1];
        var offsets = new double[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var offsetPx = -profileHalfLengthPx + (i / (double)steps) * profileHalfLengthPx * 2;
            var x = centrePoint.X + normal.X * offsetPx;
            var y = centrePoint.Y + normal.Y * offsetPx;
            values[i] = StatisticsAndGeometry.BilinearClamped(data, width, height, x, y);
            offsets[i] = offsetPx * nmPerPixel;
        }
        return (values, offsets);
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

    private sealed class PeakInfo
    {
        public int Index { get; init; }
        public double Height { get; init; }
        public double OffsetNm { get; init; }
        public double Baseline { get; init; }
    }

    private static List<PeakInfo> FindPeaks(IReadOnlyList<double> values, IReadOnlyList<double> offsetsNm)
    {
        var peaks = new List<PeakInfo>();
        if (values.Count < 3) return peaks;
        var edge = Math.Max(2, (int)Math.Floor(values.Count * 0.12));
        var baseline = (StatisticsAndGeometry.Mean(values.Take(edge).ToArray()) + StatisticsAndGeometry.Mean(values.Skip(values.Count - edge).Take(edge).ToArray())) / 2.0;
        var max = values.Max();
        var threshold = baseline + Math.Max((max - baseline) * 0.08, 1e-3);
        for (var i = 1; i < values.Count - 1; i++)
        {
            if (values[i] < values[i - 1] || values[i] < values[i + 1] || values[i] < threshold) continue;
            peaks.Add(new PeakInfo { Index = i, Height = values[i], OffsetNm = offsetsNm[i], Baseline = baseline });
        }
        return peaks.OrderByDescending(p => p.Height).ToList();
    }

    private static double ComputePeakFwhm(IReadOnlyList<double> values, IReadOnlyList<double> offsetsNm, int peakIndex, double baseline)
    {
        var peak = values[peakIndex];
        var half = baseline + (peak - baseline) / 2.0;
        var li = peakIndex;
        var ri = peakIndex;
        while (li > 0 && values[li] >= half) li--;
        while (ri < values.Count - 1 && values[ri] >= half) ri++;
        var left = InterpolateCrossing(values, offsetsNm, li, Math.Min(values.Count - 1, li + 1), half);
        var right = InterpolateCrossing(values, offsetsNm, Math.Max(0, ri - 1), ri, half);
        return Math.Max(0, right - left);
    }

    private static double InterpolateCrossing(IReadOnlyList<double> values, IReadOnlyList<double> offsetsNm, int i0, int i1, double target)
    {
        var v0 = values[i0];
        var v1 = values[i1];
        var x0 = offsetsNm[i0];
        var x1 = offsetsNm[i1];
        if (Math.Abs(v1 - v0) < 1e-9) return x0;
        var t = StatisticsAndGeometry.Clamp((target - v0) / (v1 - v0), 0, 1);
        return x0 + (x1 - x0) * t;
    }

    private static double ComputePeakSeparationWidthNm(IReadOnlyList<PeakInfo> peaks)
    {
        if (peaks.Count < 2) return 0;
        var dominant = peaks
            .OrderByDescending(peak => peak.Height)
            .Take(2)
            .OrderBy(peak => peak.OffsetNm)
            .ToArray();
        return dominant.Length == 2 ? Math.Max(0, Math.Abs(dominant[1].OffsetNm - dominant[0].OffsetNm)) : 0;
    }

    private static double ComputeMorphologyWidthNm(IReadOnlyList<PeakInfo> peaks, IReadOnlyList<double> values, IReadOnlyList<double> offsetsNm, PeakInfo? strongest)
    {
        var separationWidth = ComputePeakSeparationWidthNm(peaks);
        if (separationWidth > 1e-9) return separationWidth;
        return strongest is null ? 0 : ComputePeakFwhm(values, offsetsNm, strongest.Index, strongest.Baseline);
    }

    private static double EstimatePeakSeparation(PiecrustFileState file, IReadOnlyList<(PointD Point, double ArcNm)> sampled, double halfWidthPx)
    {
        if (sampled.Count == 0) return 0;
        var mid = sampled[sampled.Count / 2];
        var tangent = StatisticsAndGeometry.GetCurveTangent(sampled, sampled.Count / 2);
        var profile = GetPerpendicularProfile(file.HeightData, file.PixelWidth, file.PixelHeight, mid.Point, tangent, halfWidthPx, Math.Max(0.25, 1.0 / Math.Max(1e-9, file.NmPerPixel)), file.NmPerPixel);
        var corrected = BaselineCorrect(StatisticsAndGeometry.MovingGaussianSmooth(profile.Values, 2));
        var peaks = FindPeaks(corrected, profile.OffsetsNm).Take(2).OrderBy(p => p.OffsetNm).ToArray();
        return peaks.Length == 2 ? Math.Abs(peaks[1].OffsetNm - peaks[0].OffsetNm) : 0;
    }

    private static double EstimateDipDepth(PiecrustFileState file, IReadOnlyList<(PointD Point, double ArcNm)> sampled, double halfWidthPx)
    {
        if (sampled.Count == 0) return 0;
        var mid = sampled[sampled.Count / 2];
        var tangent = StatisticsAndGeometry.GetCurveTangent(sampled, sampled.Count / 2);
        var profile = GetPerpendicularProfile(file.HeightData, file.PixelWidth, file.PixelHeight, mid.Point, tangent, halfWidthPx, Math.Max(0.25, 1.0 / Math.Max(1e-9, file.NmPerPixel)), file.NmPerPixel);
        var corrected = BaselineCorrect(StatisticsAndGeometry.MovingGaussianSmooth(profile.Values, 2));
        var peaks = FindPeaks(corrected, profile.OffsetsNm).Take(2).OrderBy(p => p.OffsetNm).ToArray();
        if (peaks.Length != 2) return 0;
        var leftIndex = peaks[0].Index;
        var rightIndex = peaks[1].Index;
        if (rightIndex <= leftIndex) return 0;
        var dip = corrected.Skip(leftIndex).Take(rightIndex - leftIndex + 1).DefaultIfEmpty(0).Min();
        return Math.Max(0, ((peaks[0].Height + peaks[1].Height) / 2.0) - dip);
    }

    private static double[] ShiftToZero(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var min = values.Min();
        var output = new double[values.Count];
        for (var i = 0; i < values.Count; i++) output[i] = Math.Max(0, values[i] - min);
        return output;
    }

    private static double[] ExtractDoubleGaussian(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        var mid = values.Count / 2;
        var left = values.Take(mid).ToArray();
        var right = values.Skip(mid).ToArray();
        var l = FitHalf(left, 0);
        var r = FitHalf(right, mid);
        return new[] { l.Amplitude, l.Mu, l.Sigma, r.Amplitude, r.Mu, r.Sigma };
    }

    private static (double Amplitude, double Mu, double Sigma) FitHalf(IReadOnlyList<double> values, int offset)
    {
        if (values.Count == 0) return (0, offset, 1);
        var max = values.Max();
        var maxIndex = Array.IndexOf(values.ToArray(), max);
        var half = max / 2.0;
        var lo = maxIndex;
        var hi = maxIndex;
        while (lo > 0 && values[lo] > half) lo--;
        while (hi < values.Count - 1 && values[hi] > half) hi++;
        var fwhm = Math.Max(1, hi - lo);
        var sigma = fwhm / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)));
        return (max, maxIndex + offset, Math.Max(0.5, sigma));
    }

    private static double[] BuildCenteredGaussianTemplate(IReadOnlyList<double> parameters, int count)
    {
        if (parameters.Count < 6 || count <= 0) return Array.Empty<double>();
        var amplitudeLeft = Math.Max(0, parameters[0]);
        var muLeft = parameters[1];
        var sigmaLeft = Math.Max(0.5, Math.Abs(parameters[2]));
        var amplitudeRight = Math.Max(0, parameters[3]);
        var muRight = parameters[4];
        var sigmaRight = Math.Max(0.5, Math.Abs(parameters[5]));
        var separation = Math.Max(0, Math.Abs(muRight - muLeft));
        var center = (count - 1) / 2.0;
        var leftCenter = center - separation / 2.0;
        var rightCenter = center + separation / 2.0;
        var output = new double[count];
        for (var i = 0; i < count; i++)
        {
            output[i] =
                amplitudeLeft * Math.Exp(-Math.Pow(i - leftCenter, 2) / (2 * sigmaLeft * sigmaLeft)) +
                amplitudeRight * Math.Exp(-Math.Pow(i - rightCenter, 2) / (2 * sigmaRight * sigmaRight));
        }

        return ShiftToZero(output);
    }

    private static double[]? AverageVectors(IEnumerable<double[]> vectors)
    {
        var valid = vectors.Where(v => v is { Length: > 0 }).ToArray();
        if (valid.Length == 0) return null;
        var n = valid[0].Length;
        var output = new double[n];
        foreach (var vector in valid)
        {
            for (var i = 0; i < n; i++) output[i] += vector[i];
        }
        for (var i = 0; i < n; i++) output[i] /= valid.Length;
        return output;
    }

    private static double[] Resample(IReadOnlyList<double> values, int count)
    {
        if (values.Count == 0 || count <= 0) return Array.Empty<double>();
        if (values.Count == count) return values.ToArray();
        if (values.Count == 1) return Enumerable.Repeat(values[0], count).ToArray();
        var output = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = i * (values.Count - 1.0) / Math.Max(1, count - 1);
            var lo = (int)Math.Floor(t);
            var hi = Math.Min(values.Count - 1, lo + 1);
            var mix = t - lo;
            output[i] = values[lo] * (1 - mix) + values[hi] * mix;
        }
        return output;
    }

    private static (PiecrustFileState[] Files, double[] Positions) BuildOrderedSimulationReferences(IReadOnlyList<PiecrustFileState> files, PiecrustFileState startFile, PiecrustFileState endFile)
    {
        var low = Math.Min(startFile.SequenceOrder, endFile.SequenceOrder);
        var high = Math.Max(startFile.SequenceOrder, endFile.SequenceOrder);
        var ascending = startFile.SequenceOrder <= endFile.SequenceOrder;

        var selected = files
            .Where(file => file.HeightData.Length > 0 && file.SequenceOrder >= low && file.SequenceOrder <= high)
            .OrderBy(file => ascending ? file.SequenceOrder : -file.SequenceOrder)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!selected.Contains(startFile)) selected.Add(startFile);
        if (!selected.Contains(endFile)) selected.Add(endFile);
        selected = selected
            .Distinct()
            .OrderBy(file => ascending ? file.SequenceOrder : -file.SequenceOrder)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count < 2)
        {
            return (Array.Empty<PiecrustFileState>(), Array.Empty<double>());
        }

        var hasDuplicateSequence = selected.GroupBy(file => file.SequenceOrder).Any(group => group.Count() > 1);
        var positions = new double[selected.Count];
        if (hasDuplicateSequence || startFile.SequenceOrder == endFile.SequenceOrder)
        {
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = positions.Length == 1 ? 0 : i / (double)(positions.Length - 1);
            }
        }
        else
        {
            var denominator = endFile.SequenceOrder - startFile.SequenceOrder;
            for (var i = 0; i < selected.Count; i++)
            {
                positions[i] = (selected[i].SequenceOrder - startFile.SequenceOrder) / (double)denominator;
            }
        }

        return (selected.ToArray(), positions);
    }

    private sealed class CrossSectionDescriptors
    {
        public double MeanHeightNm { get; init; }
        public double MeanWidthNm { get; init; }
        public double HeightWidthRatio { get; init; }
        public double PeakSeparationNm { get; init; }
        public double DipDepthNm { get; init; }
        public double RoughnessNm { get; init; }
    }

    private GrowthPredictionContext BuildGrowthPredictionContext(IReadOnlyList<PiecrustFileState> orderedFiles, IReadOnlyList<double> orderedPositions, double progress, double[] currentFrame, int width, int height, double scanSizeNmX)
    {
        var estimated = EstimateCrossSectionDescriptors(currentFrame, width, height, scanSizeNmX);
        return new GrowthPredictionContext(
            StatisticsAndGeometry.Clamp(progress, 0, 1),
            StatisticsAndGeometry.Clamp(progress, 0, 1),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => string.Equals(file.ConditionType, "control", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, 0),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => string.Equals(file.ConditionType, "treated", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, 0),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => Math.Log(1 + Math.Max(0, file.AntibioticDoseUgPerMl)), 0),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.MeanHeightNm, estimated.MeanHeightNm),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.MeanWidthNm, estimated.MeanWidthNm),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.HeightToWidthRatio, estimated.HeightWidthRatio),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.PeakSeparationNm, estimated.PeakSeparationNm),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.DipDepthNm, estimated.DipDepthNm),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.RoughnessNm, estimated.RoughnessNm),
            InterpolateReferenceFeature(orderedFiles, orderedPositions, progress, file => file.GuidedSummary?.Continuity, 1.0));
    }

    private static double InterpolateReferenceFeature(IReadOnlyList<PiecrustFileState> orderedFiles, IReadOnlyList<double> orderedPositions, double progress, Func<PiecrustFileState, double?> selector, double fallback)
    {
        var samples = new List<(double Position, double Value)>(orderedFiles.Count);
        for (var i = 0; i < orderedFiles.Count; i++)
        {
            var value = selector(orderedFiles[i]);
            if (value is null || !double.IsFinite(value.Value)) continue;
            samples.Add((orderedPositions[i], value.Value));
        }

        if (samples.Count == 0) return fallback;
        if (samples.Count == 1) return samples[0].Value;
        progress = StatisticsAndGeometry.Clamp(progress, 0, 1);
        if (progress <= samples[0].Position) return samples[0].Value;
        if (progress >= samples[^1].Position) return samples[^1].Value;

        for (var i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if (progress > b.Position) continue;
            var mix = StatisticsAndGeometry.Clamp((progress - a.Position) / Math.Max(1e-9, b.Position - a.Position), 0, 1);
            return a.Value * (1 - mix) + b.Value * mix;
        }

        return samples[^1].Value;
    }

    private CrossSectionDescriptors EstimateCrossSectionDescriptors(double[] currentFrame, int width, int height, double scanSizeNmX)
    {
        var profile = BuildSurfaceCrossSection(currentFrame, width, height, scanSizeNmX);
        if (profile.Count == 0)
        {
            return new CrossSectionDescriptors();
        }

        var rawValues = profile.Select(point => point.Y).ToArray();
        var smoothed = StatisticsAndGeometry.MovingGaussianSmooth(rawValues, 3);
        var corrected = ShiftToZero(BaselineCorrect(smoothed));
        var offsetsNm = profile.Select(point => point.X).ToArray();
        var peaks = FindPeaks(corrected, offsetsNm).OrderBy(peak => peak.OffsetNm).ToArray();
        var strongest = peaks.OrderByDescending(peak => peak.Height).FirstOrDefault();
        var peakSeparationNm = ComputePeakSeparationWidthNm(peaks);
        var widthNm = ComputeMorphologyWidthNm(peaks, corrected, offsetsNm, strongest);
        var dipDepthNm = 0.0;
        if (peaks.Length >= 2)
        {
            var leftIndex = peaks[0].Index;
            var rightIndex = peaks[^1].Index;
            var dip = corrected.Skip(leftIndex).Take(rightIndex - leftIndex + 1).DefaultIfEmpty(0).Min();
            dipDepthNm = Math.Max(0, ((peaks[0].Height + peaks[^1].Height) / 2.0) - dip);
        }

        double roughness = 0;
        for (var i = 0; i < rawValues.Length; i++) roughness += Math.Abs(rawValues[i] - smoothed[i]);
        roughness /= Math.Max(1, rawValues.Length);

        var meanHeightNm = corrected.Length == 0 ? 0 : corrected.Max();
        return new CrossSectionDescriptors
        {
            MeanHeightNm = meanHeightNm,
            MeanWidthNm = widthNm,
            HeightWidthRatio = meanHeightNm / Math.Max(1e-9, widthNm),
            PeakSeparationNm = peakSeparationNm,
            DipDepthNm = dipDepthNm,
            RoughnessNm = roughness
        };
    }

    private static double[] BuildCenteredGaussianTemplateFromShape(IReadOnlyList<double> shape, int count)
    {
        if (shape.Count < 5 || count <= 0) return Array.Empty<double>();
        var amplitudeLeft = Math.Max(0, shape[0]);
        var sigmaLeft = Math.Max(0.5, StatisticsAndGeometry.Clamp(shape[1], 0.01, 0.35) * Math.Max(1.0, count - 1.0));
        var amplitudeRight = Math.Max(0, shape[2]);
        var sigmaRight = Math.Max(0.5, StatisticsAndGeometry.Clamp(shape[3], 0.01, 0.35) * Math.Max(1.0, count - 1.0));
        var separation = StatisticsAndGeometry.Clamp(shape[4], 0.02, 0.90) * Math.Max(1.0, count - 1.0);
        var center = (count - 1) / 2.0;
        var leftCenter = center - separation / 2.0;
        var rightCenter = center + separation / 2.0;
        var output = new double[count];
        for (var i = 0; i < count; i++)
        {
            output[i] =
                amplitudeLeft * Math.Exp(-Math.Pow(i - leftCenter, 2) / (2 * sigmaLeft * sigmaLeft)) +
                amplitudeRight * Math.Exp(-Math.Pow(i - rightCenter, 2) / (2 * sigmaRight * sigmaRight));
        }

        return ShiftToZero(output);
    }

    private static double[] ApplyBimodalTrajectoryGuidance(double[] frame, int width, int height, IReadOnlyList<double> predictedProfile, double blendWeight) =>
        ApplyProfileGuidance(frame, width, height, predictedProfile, blendWeight);

    private static double[] ApplySupervisedProfileGuidance(double[] frame, int width, int height, IReadOnlyList<double> predictedProfile, double blendWeight)
    {
        if (frame.Length == 0 || width <= 0 || height <= 0 || predictedProfile.Count != width || !(blendWeight > 1e-6)) return frame;
        return ApplyProfileGuidance(frame, width, height, predictedProfile, blendWeight);
    }

    private static double[] ApplyProfileGuidance(double[] frame, int width, int height, IReadOnlyList<double> predictedProfile, double blendWeight)
    {
        if (frame.Length == 0 || width <= 0 || height <= 0 || predictedProfile.Count != width || !(blendWeight > 1e-6)) return frame;
        var currentProfile = BuildHorizontalPeakProfile(frame, width, height);
        var currentShape = ShiftToZero(BaselineCorrect(StatisticsAndGeometry.MovingGaussianSmooth(currentProfile, 3)));
        if (currentShape.Length != predictedProfile.Count) return frame;

        var output = new double[frame.Length];
        Array.Copy(frame, output, frame.Length);
        var centerY = (height - 1) / 2.0;
        var sigmaY = Math.Max(6.0, height * 0.22);

        for (var y = 0; y < height; y++)
        {
            var verticalWeight = Math.Exp(-Math.Pow(y - centerY, 2) / (2 * sigmaY * sigmaY));
            for (var x = 0; x < width; x++)
            {
                var delta = predictedProfile[x] - currentShape[x];
                output[y * width + x] += delta * blendWeight * verticalWeight;
            }
        }

        var min = output.Min();
        if (min < 0)
        {
            for (var i = 0; i < output.Length; i++) output[i] -= min;
        }

        return output;
    }

    private static double GetCenteredAxisPositionNm(int index, int count, double scanSizeNmX)
    {
        if (count <= 1) return 0;
        var fraction = index / Math.Max(1.0, count - 1.0);
        return -scanSizeNmX / 2.0 + fraction * scanSizeNmX;
    }

    private static double StageTo01(string stage) => stage switch
    {
        "early" => 0.15,
        "middle" => 0.55,
        "late" => 0.90,
        _ => 0.50
    };

    private static double[] ResampleSurface(double[] data, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (data.Length == 0 || sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0) return Array.Empty<double>();
        var output = new double[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = targetHeight == 1 ? 0 : y * (sourceHeight - 1.0) / (targetHeight - 1.0);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = targetWidth == 1 ? 0 : x * (sourceWidth - 1.0) / (targetWidth - 1.0);
                output[y * targetWidth + x] = StatisticsAndGeometry.BilinearClamped(data, sourceWidth, sourceHeight, sourceX, sourceY);
            }
        }

        return output;
    }

    private static double ComputeRemovalSignal(GuidedSummary summary)
    {
        return Math.Max(0, summary.MeanWidthNm * 0.15 + summary.RoughnessNm + summary.DipDepthNm * 0.35);
    }

    private static PiecrustFileState[] SelectControlReferenceFiles(IReadOnlyList<PiecrustFileState> files, PiecrustFileState file)
    {
        var sameStageControls = files
            .Where(candidate =>
                candidate.GuidedSummary is not null &&
                string.Equals(candidate.ConditionType, "control", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Stage, file.Stage, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sameStageControls.Length > 0) return sameStageControls;

        return files
            .Where(candidate =>
                candidate.GuidedSummary is not null &&
                string.Equals(candidate.ConditionType, "control", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private double ComputeControlProfileDeviation01(PiecrustFileState file, IReadOnlyList<PiecrustFileState> controls)
    {
        var subject = GetReferenceProfile(file);
        if (subject.Length == 0) return 0;

        var controlProfiles = controls
            .Select(GetReferenceProfile)
            .Where(profile => profile.Length > 0)
            .ToArray();
        if (controlProfiles.Length == 0) return 0;

        var targetLength = new[] { subject.Length }.Concat(controlProfiles.Select(profile => profile.Length)).Max();
        var subjectResampled = subject.Length == targetLength ? subject : Resample(subject, targetLength);
        var controlMean = AverageVectors(controlProfiles.Select(profile => profile.Length == targetLength ? profile : Resample(profile, targetLength)));
        if (controlMean is null || controlMean.Length == 0) return 0;

        double sum = 0;
        for (var i = 0; i < targetLength; i++)
        {
            var delta = subjectResampled[i] - controlMean[i];
            sum += delta * delta;
        }

        var rmse = Math.Sqrt(sum / Math.Max(1, targetLength));
        var controlScale = Math.Max(1e-9, controlMean.Max());
        return StatisticsAndGeometry.Clamp(rmse / controlScale, 0, 1);
    }

    private static (double Mean, double Error) BuildStageUncertainty(IReadOnlyList<PiecrustFileState> stageFiles, Func<PiecrustFileState, double> meanSelector, Func<PiecrustFileState, double> semSelector)
    {
        if (stageFiles.Count == 0) return (0, 0);
        var means = stageFiles.Select(meanSelector).Where(double.IsFinite).ToArray();
        if (means.Length == 0) return (0, 0);
        var stageMean = StatisticsAndGeometry.Mean(means);
        if (stageFiles.Count == 1)
        {
            return (stageMean, Math.Max(0, semSelector(stageFiles[0])));
        }

        var betweenSem = StatisticsAndGeometry.StandardError(means);
        var withinRms = Math.Sqrt(stageFiles
            .Select(file => Math.Pow(Math.Max(0, semSelector(file)), 2))
            .Average());
        var withinSem = withinRms / Math.Sqrt(Math.Max(1, stageFiles.Count));
        var combined = Math.Sqrt(betweenSem * betweenSem + withinSem * withinSem);
        return (stageMean, combined);
    }

    private static IReadOnlyList<PiecrustFileState> OrderFilesForPerImageBoxPlots(IEnumerable<PiecrustFileState> files) =>
        files
            .Where(file => file.GuidedSummary is not null)
            .OrderBy(file => file.SequenceOrder)
            .ThenBy(file => Array.IndexOf(StageOrder, (file.Stage ?? string.Empty).ToLowerInvariant()))
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string GetStageColor(string stage, string fallback) =>
        stage.ToLowerInvariant() switch
        {
            "early" => "#6b9358",
            "middle" => "#bf8741",
            "late" => "#9d5b4d",
            _ => fallback
        };

    private static double GetGuideProfileHalfWidthPx(PiecrustFileState file, double widthExpansionFraction = 0.10)
    {
        var expandedWidthNm = file.GuideCorridorWidthNm * (1.0 + widthExpansionFraction);
        return Math.Max(2.0, expandedWidthNm / Math.Max(1e-9, file.NmPerPixel) / 2.0);
    }

    private static bool HasUsableGuide(PiecrustFileState file) => file.UseManualGuide && file.GuideLineFinished && file.GuidePoints.Count >= 2;

    private static PointD ComputeGuideDirection(IReadOnlyList<PointD> points)
    {
        if (points.Count < 2) return new PointD(1, 0);
        var start = points[0];
        var end = points[^1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len > 1e-9 ? new PointD(dx / len, dy / len) : new PointD(1, 0);
    }

    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;

    private static GuideAlignmentInfo? BuildGuideAlignmentInfo(PiecrustFileState file, PointD targetDirection)
    {
        if (!HasUsableGuide(file)) return null;
        var direction = ComputeGuideDirection(file.GuidePoints);
        var rotationRadians = Math.Atan2(targetDirection.Y, targetDirection.X) - Math.Atan2(direction.Y, direction.X);
        var guideCenterNm = EstimateGuideCentroidNm(file);
        var scanSizeXNm = GetScanSizeXNm(file);
        var scanSizeYNm = GetScanSizeYNm(file);
        var relativeCorners = new[]
        {
            new PointD(-guideCenterNm.X, -guideCenterNm.Y),
            new PointD(scanSizeXNm - guideCenterNm.X, -guideCenterNm.Y),
            new PointD(-guideCenterNm.X, scanSizeYNm - guideCenterNm.Y),
            new PointD(scanSizeXNm - guideCenterNm.X, scanSizeYNm - guideCenterNm.Y)
        };

        var rotatedCorners = relativeCorners.Select(corner => Rotate(corner, rotationRadians)).ToArray();
        var minX = rotatedCorners.Min(corner => corner.X);
        var maxX = rotatedCorners.Max(corner => corner.X);
        var minY = rotatedCorners.Min(corner => corner.Y);
        var maxY = rotatedCorners.Max(corner => corner.Y);

        return new GuideAlignmentInfo(
            rotationRadians,
            guideCenterNm,
            minX,
            maxX,
            minY,
            maxY,
            EstimateQuantile(file.HeightData, 0.02));
    }

    private static double[] ResampleGuideAlignedFullImage(PiecrustFileState file, GuideAlignmentInfo alignment, double globalMinXNm, double globalMinYNm, double targetWidthNm, double targetHeightNm, int targetWidth, int targetHeight)
    {
        var output = new double[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var alignedYNm = targetHeight == 1 ? globalMinYNm : globalMinYNm + y * targetHeightNm / (targetHeight - 1.0);
            for (var x = 0; x < targetWidth; x++)
            {
                var alignedXNm = targetWidth == 1 ? globalMinXNm : globalMinXNm + x * targetWidthNm / (targetWidth - 1.0);
                var sourceRelativeNm = Rotate(new PointD(alignedXNm, alignedYNm), -alignment.RotationRadians);
                var sourceNmX = alignment.GuideCenterNm.X + sourceRelativeNm.X;
                var sourceNmY = alignment.GuideCenterNm.Y + sourceRelativeNm.Y;
                var sourcePxX = sourceNmX / Math.Max(1e-9, file.NmPerPixel);
                var sourcePxY = sourceNmY / Math.Max(1e-9, file.NmPerPixel);
                output[y * targetWidth + x] = SampleSurfaceOrBackground(file.HeightData, file.PixelWidth, file.PixelHeight, sourcePxX, sourcePxY, alignment.BackgroundValue);
            }
        }

        return output;
    }

    private static double[] ResampleGuidedCorridorSurface(PiecrustFileState file, double targetWidthNm, double targetHeightNm, int targetWidth, int targetHeight)
    {
        if (!HasUsableGuide(file) || file.HeightData.Length == 0 || targetWidth <= 0 || targetHeight <= 0) return Array.Empty<double>();
        var sampleIntervalNm = targetHeight <= 1 ? targetHeightNm : targetHeightNm / Math.Max(1, targetHeight - 1);
        var sampled = StatisticsAndGeometry.SampleCurveAtPhysicalInterval(file.GuidePoints, Math.Max(file.NmPerPixel * 0.5, sampleIntervalNm), file.NmPerPixel);
        if (sampled.Count < 2) return Array.Empty<double>();

        var output = new double[targetWidth * targetHeight];
        for (var row = 0; row < targetHeight; row++)
        {
            var arcNm = targetHeight == 1 ? 0 : row * targetHeightNm / Math.Max(1, targetHeight - 1.0);
            var (point, tangent) = InterpolateGuideFrame(sampled, arcNm);
            var normal = new PointD(-tangent.Y, tangent.X);
            for (var column = 0; column < targetWidth; column++)
            {
                var offsetNm = targetWidth == 1 ? 0 : -targetWidthNm / 2.0 + column * targetWidthNm / Math.Max(1, targetWidth - 1.0);
                var offsetPx = offsetNm / Math.Max(1e-9, file.NmPerPixel);
                var x = point.X + normal.X * offsetPx;
                var y = point.Y + normal.Y * offsetPx;
                output[row * targetWidth + column] = SampleSurfaceOrBackground(file.HeightData, file.PixelWidth, file.PixelHeight, x, y, 0);
            }
        }

        return output;
    }

    private static (PointD Point, PointD Tangent) InterpolateGuideFrame(IReadOnlyList<(PointD Point, double ArcNm)> sampled, double arcNm)
    {
        if (sampled.Count == 0) return (new PointD(0, 0), new PointD(1, 0));
        if (arcNm <= sampled[0].ArcNm) return (sampled[0].Point, StatisticsAndGeometry.GetCurveTangent(sampled, 0));
        if (arcNm >= sampled[^1].ArcNm) return (sampled[^1].Point, StatisticsAndGeometry.GetCurveTangent(sampled, sampled.Count - 1));

        for (var i = 1; i < sampled.Count; i++)
        {
            var previous = sampled[i - 1];
            var current = sampled[i];
            if (arcNm > current.ArcNm) continue;

            var span = Math.Max(1e-9, current.ArcNm - previous.ArcNm);
            var mix = StatisticsAndGeometry.Clamp((arcNm - previous.ArcNm) / span, 0, 1);
            var point = new PointD(
                previous.Point.X + (current.Point.X - previous.Point.X) * mix,
                previous.Point.Y + (current.Point.Y - previous.Point.Y) * mix);
            var dx = current.Point.X - previous.Point.X;
            var dy = current.Point.Y - previous.Point.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            var tangent = length > 1e-9 ? new PointD(dx / length, dy / length) : StatisticsAndGeometry.GetCurveTangent(sampled, i);
            return (point, tangent);
        }

        return (sampled[^1].Point, StatisticsAndGeometry.GetCurveTangent(sampled, sampled.Count - 1));
    }

    private static double[] PrepareSimulationSurface(double[] surface, int width, int height)
    {
        var output = new double[surface.Length];
        Array.Copy(surface, output, surface.Length);
        var baseline = EstimateQuantile(output, 0.05);
        for (var i = 0; i < output.Length; i++) output[i] -= baseline;
        var min = output.Length == 0 ? 0 : output.Min();
        if (min < 0)
        {
            for (var i = 0; i < output.Length; i++) output[i] -= min;
        }

        return output;
    }

    private static double[] CenterSurfaceByBimodalProfile(double[] surface, int width, int height)
    {
        if (surface.Length == 0 || width <= 0 || height <= 0) return surface;
        var profile = BuildHorizontalPeakProfile(surface, width, height);
        if (profile.Length == 0) return surface;

        var offsets = Enumerable.Range(0, profile.Length).Select(index => (double)index).ToArray();
        var smooth = StatisticsAndGeometry.MovingGaussianSmooth(profile, 3);
        var peaks = FindPeaks(smooth, offsets).Take(2).OrderBy(peak => peak.OffsetNm).ToArray();

        double anchorIndex;
        if (peaks.Length >= 2)
        {
            var left = peaks[0].Index;
            var right = peaks[1].Index;
            var dipIndex = left;
            var dipValue = smooth[left];
            for (var i = left + 1; i <= right; i++)
            {
                if (smooth[i] >= dipValue) continue;
                dipValue = smooth[i];
                dipIndex = i;
            }

            anchorIndex = RefineWeightedPosition(smooth, Math.Max(0, dipIndex - 2), Math.Min(smooth.Length - 1, dipIndex + 2));
        }
        else
        {
            var peakIndex = 0;
            var peakValue = smooth[0];
            for (var i = 1; i < smooth.Length; i++)
            {
                if (smooth[i] <= peakValue) continue;
                peakValue = smooth[i];
                peakIndex = i;
            }

            if (!(peakValue > 0)) return surface;
            anchorIndex = RefineWeightedPosition(smooth, Math.Max(0, peakIndex - 2), Math.Min(smooth.Length - 1, peakIndex + 2));
        }

        var rowProfile = BuildVerticalMassProfile(surface, width, height);
        var rowStart = Math.Max(0, height / 2 - 6);
        var rowEnd = Math.Min(height - 1, height / 2 + 6);
        var rowAnchor = RefineWeightedPosition(rowProfile, rowStart, rowEnd);

        var targetCenterX = (width - 1) / 2.0;
        var targetCenterY = (height - 1) / 2.0;
        var shiftX = targetCenterX - anchorIndex;
        var shiftY = targetCenterY - rowAnchor;
        if (Math.Abs(shiftX) < 1e-3 && Math.Abs(shiftY) < 1e-3) return surface;

        var output = new double[surface.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - shiftX;
                var sourceY = y - shiftY;
                output[y * width + x] = SampleSurfaceOrBackground(surface, width, height, sourceX, sourceY, 0);
            }
        }

        return output;
    }

    private static double[] BuildHorizontalPeakProfile(double[] surface, int width, int height)
    {
        var profile = new double[width];
        var rowCenter = Math.Clamp(height / 2, 0, Math.Max(0, height - 1));
        var rowStart = Math.Max(0, rowCenter - 2);
        var rowEnd = Math.Min(height - 1, rowCenter + 2);
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            var count = 0;
            for (var y = rowStart; y <= rowEnd; y++)
            {
                sum += surface[y * width + x];
                count++;
            }

            profile[x] = count == 0 ? 0 : sum / count;
        }

        return profile;
    }

    private static double[] BuildVerticalMassProfile(double[] surface, int width, int height)
    {
        var profile = new double[height];
        for (var y = 0; y < height; y++)
        {
            double sum = 0;
            for (var x = 0; x < width; x++)
            {
                sum += Math.Max(0, surface[y * width + x]);
            }

            profile[y] = sum / Math.Max(1, width);
        }

        return profile;
    }

    private static double[] AlignSurfaceToReference(double[] surface, double[] reference, int width, int height, int maxShift = 18)
    {
        if (surface.Length == 0 || reference.Length != surface.Length || width <= 0 || height <= 0) return surface;

        var candidateMask = BuildAlignmentMask(surface);
        var referenceMask = BuildAlignmentMask(reference);
        var bestScore = double.NegativeInfinity;
        var bestDx = 0;
        var bestDy = 0;

        for (var dy = -maxShift; dy <= maxShift; dy++)
        {
            for (var dx = -maxShift; dx <= maxShift; dx++)
            {
                var score = ScoreShift(candidateMask, referenceMask, width, height, dx, dy);
                if (score <= bestScore) continue;
                bestScore = score;
                bestDx = dx;
                bestDy = dy;
            }
        }

        if (bestDx == 0 && bestDy == 0) return surface;

        var output = new double[surface.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                output[y * width + x] = SampleSurfaceOrBackground(surface, width, height, x - bestDx, y - bestDy, 0);
            }
        }

        return output;
    }

    private static double[] BuildAlignmentMask(double[] surface)
    {
        if (surface.Length == 0) return Array.Empty<double>();
        var positive = surface.Select(value => Math.Max(0, value)).ToArray();
        var threshold = EstimateQuantile(positive, 0.7);
        if (!(threshold > 0))
        {
            threshold = positive.Max() * 0.25;
        }

        for (var i = 0; i < positive.Length; i++)
        {
            var value = positive[i] - threshold;
            positive[i] = value > 0 ? value : 0;
        }

        return positive;
    }

    private static double ScoreShift(double[] candidate, double[] reference, int width, int height, int dx, int dy)
    {
        double numerator = 0;
        double candidateEnergy = 0;
        double referenceEnergy = 0;
        var overlap = 0;

        for (var y = 0; y < height; y++)
        {
            var sourceY = y - dy;
            if (sourceY < 0 || sourceY >= height) continue;

            for (var x = 0; x < width; x++)
            {
                var sourceX = x - dx;
                if (sourceX < 0 || sourceX >= width) continue;

                var candidateValue = candidate[sourceY * width + sourceX];
                var referenceValue = reference[y * width + x];
                numerator += candidateValue * referenceValue;
                candidateEnergy += candidateValue * candidateValue;
                referenceEnergy += referenceValue * referenceValue;
                overlap++;
            }
        }

        if (overlap == 0 || candidateEnergy <= 1e-9 || referenceEnergy <= 1e-9) return double.NegativeInfinity;
        var normalizedCorrelation = numerator / Math.Sqrt(candidateEnergy * referenceEnergy);
        return normalizedCorrelation + overlap / (double)(width * height) * 0.02;
    }

    private static double RefineWeightedPosition(IReadOnlyList<double> profile, int start, int end)
    {
        double weightedPosition = 0;
        double weightSum = 0;
        for (var i = start; i <= end; i++)
        {
            var weight = Math.Max(0, profile[i]);
            weightedPosition += i * weight;
            weightSum += weight;
        }

        return weightSum > 1e-9 ? weightedPosition / weightSum : (start + end) / 2.0;
    }

    private static double[,] BuildPolynomialProjector(IReadOnlyList<double> positions, int degree)
    {
        var rows = positions.Count;
        var cols = degree + 1;
        var design = new double[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            var term = 1.0;
            for (var c = 0; c < cols; c++)
            {
                design[r, c] = term;
                term *= positions[r];
            }
        }

        var xtx = new double[cols, cols];
        var xt = new double[cols, rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                xt[c, r] = design[r, c];
            }
        }

        for (var i = 0; i < cols; i++)
        {
            for (var j = 0; j < cols; j++)
            {
                double sum = 0;
                for (var k = 0; k < rows; k++) sum += xt[i, k] * design[k, j];
                xtx[i, j] = sum + (i == j ? 1e-8 : 0);
            }
        }

        var inverse = InvertSmallMatrix(xtx);
        var projector = new double[cols, rows];
        for (var i = 0; i < cols; i++)
        {
            for (var j = 0; j < rows; j++)
            {
                double sum = 0;
                for (var k = 0; k < cols; k++) sum += inverse[i, k] * xt[k, j];
                projector[i, j] = sum;
            }
        }

        return projector;
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

    private static double[] Multiply(double[,] matrix, IReadOnlyList<double> vector)
    {
        var rows = matrix.GetLength(0);
        var cols = matrix.GetLength(1);
        var output = new double[rows];
        for (var row = 0; row < rows; row++)
        {
            double sum = 0;
            for (var column = 0; column < cols; column++) sum += matrix[row, column] * vector[column];
            output[row] = sum;
        }

        return output;
    }

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double position)
    {
        double value = 0;
        double term = 1;
        for (var i = 0; i < coefficients.Count; i++)
        {
            value += coefficients[i] * term;
            term *= position;
        }

        return value;
    }

    private static (double Min, double Max) EstimateSurfaceRange(IReadOnlyList<double[]> frames)
    {
        var sample = new List<double>(16384);
        foreach (var frame in frames)
        {
            if (frame.Length == 0) continue;
            var step = Math.Max(1, frame.Length / 2048);
            for (var i = 0; i < frame.Length; i += step)
            {
                var value = frame[i];
                if (double.IsFinite(value)) sample.Add(value);
            }
        }

        if (sample.Count == 0) return (0, 1);
        sample.Sort();
        var lo = sample[(int)Math.Clamp(Math.Round((sample.Count - 1) * 0.02), 0, sample.Count - 1)];
        var hi = sample[(int)Math.Clamp(Math.Round((sample.Count - 1) * 0.98), 0, sample.Count - 1)];
        if (!(hi > lo))
        {
            lo = sample.First();
            hi = sample.Last();
        }

        if (!(hi > lo)) hi = lo + 1;
        return (Math.Max(0, lo), hi);
    }

    private double[] GetReferenceProfile(PiecrustFileState file)
    {
        if (file.EvolutionRecord?.Profile is { Length: > 0 } cached) return cached;
        var generated = BuildEvolutionRecord(file);
        file.EvolutionRecord = generated;
        return generated.Profile;
    }

    private static double SmoothStep(double t) => t * t * (3.0 - 2.0 * t);

    private static double[]? InterpolateStageVectors(Dictionary<string, double[]?> vectors, double progress)
    {
        var available = new List<(double T, double[] Vector)>();
        if (vectors.TryGetValue("early", out var e) && e is { Length: > 0 }) available.Add((0.15, e));
        if (vectors.TryGetValue("middle", out var m) && m is { Length: > 0 }) available.Add((0.55, m));
        if (vectors.TryGetValue("late", out var l) && l is { Length: > 0 }) available.Add((0.90, l));
        if (available.Count == 0) return null;
        if (available.Count == 1) return available[0].Vector;
        progress = StatisticsAndGeometry.Clamp(progress, 0, 1);
        if (progress <= available[0].T) return available[0].Vector;
        if (progress >= available[^1].T) return available[^1].Vector;

        for (var i = 1; i < available.Count; i++)
        {
            var a = available[i - 1];
            var b = available[i];
            if (progress > b.T) continue;
            var mix = StatisticsAndGeometry.Clamp((progress - a.T) / Math.Max(1e-9, b.T - a.T), 0, 1);
            mix = SmoothStep(mix);
            return a.Vector.Select((v, index) => v * (1 - mix) + b.Vector[index] * mix).ToArray();
        }

        return available[^1].Vector;
    }

    private static PointD EstimateGuideCentroidNm(PiecrustFileState file)
    {
        if (file.GuidePoints.Count == 0) return new PointD(GetScanSizeXNm(file) / 2.0, GetScanSizeYNm(file) / 2.0);
        var x = file.GuidePoints.Average(point => point.X) * file.NmPerPixel;
        var y = file.GuidePoints.Average(point => point.Y) * file.NmPerPixel;
        return new PointD(x, y);
    }

    private static double GetScanSizeXNm(PiecrustFileState file) =>
        Math.Max(file.ScanSizeNm, file.NmPerPixel * Math.Max(1, file.PixelWidth - 1));

    private static double GetScanSizeYNm(PiecrustFileState file) =>
        Math.Max(file.NmPerPixel * Math.Max(1, file.PixelHeight - 1), GetScanSizeXNm(file) * file.PixelHeight / Math.Max(1.0, file.PixelWidth));

    private static PointD Rotate(PointD point, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new PointD(
            point.X * cos - point.Y * sin,
            point.X * sin + point.Y * cos);
    }

    private static double SampleSurfaceOrBackground(double[] data, int width, int height, double x, double y, double background)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1) return background;
        return StatisticsAndGeometry.BilinearClamped(data, width, height, x, y);
    }

    private static double EstimateQuantile(IReadOnlyList<double> values, double quantile)
    {
        if (values.Count == 0) return 0;
        var step = Math.Max(1, values.Count / 4096);
        var sample = new List<double>(Math.Min(values.Count, 4096));
        for (var i = 0; i < values.Count; i += step)
        {
            var value = values[i];
            if (double.IsFinite(value)) sample.Add(value);
        }

        if (sample.Count == 0) return 0;
        sample.Sort();
        var index = (int)Math.Clamp(Math.Round((sample.Count - 1) * quantile), 0, sample.Count - 1);
        return sample[index];
    }
}
