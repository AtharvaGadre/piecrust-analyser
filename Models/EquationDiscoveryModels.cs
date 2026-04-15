namespace PiecrustAnalyser.CSharp.Models;

public sealed class EquationDiscoveryRequest
{
    public string SampleId { get; init; } = "piecrust-session";
    public string TimeMode { get; init; } = "pseudotime_stage_ordered";
    public string ProfileMode { get; init; } = "centerline_arc_length_profile";
    public Dictionary<string, double> StageMapping { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public EquationDiscoveryOptions Options { get; init; } = new();
    public IReadOnlyList<EquationDiscoveryProfileInput> Files { get; init; } = Array.Empty<EquationDiscoveryProfileInput>();
}

public sealed class EquationDiscoveryOptions
{
    public int SpatialGridCount { get; init; } = 220;
    public int BootstrapCount { get; init; } = 20;
    public double StageJitter { get; init; } = 0.10;
    public double SampleSpacingNm { get; init; } = 1.0;
    public string DerivativeMode { get; init; } = "savitzky_golay";
    public string SparseBackend { get; init; } = "stlsq";
}

public sealed class EquationDiscoveryProfileInput
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Stage { get; init; } = "early";
    public string ConditionType { get; init; } = "unassigned";
    public string Unit { get; init; } = "nm";
    public double DoseUgPerMl { get; init; }
    public double ScanSizeNm { get; init; }
    public double NmPerPixel { get; init; }
    public double MeanHeightNm { get; init; }
    public double MeanWidthNm { get; init; }
    public double HeightToWidthRatio { get; init; }
    public double RoughnessNm { get; init; }
    public double PeakSeparationNm { get; init; }
    public double DipDepthNm { get; init; }
    public double CompromiseRatio { get; init; }
    public IReadOnlyList<double> XNm { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> YNm { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> SNm { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> ZNm { get; init; } = Array.Empty<double>();
}

public sealed class EquationDiscoveryResult
{
    public string SampleId { get; init; } = string.Empty;
    public string TimeMode { get; init; } = "pseudotime_stage_ordered";
    public string ProfileMode { get; init; } = "centerline_arc_length_profile";
    public string SpatialCoordinateLabel { get; init; } = "Aligned arc length s [nm]";
    public string HeightLabel { get; init; } = "Height [nm]";
    public string StageMappingMode { get; init; } = "fixed_stage_anchor";
    public string MetaModelSummary { get; init; } = string.Empty;
    public int MetaModelExampleCount { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
    public Dictionary<string, double> StageMapping { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<EquationDiscoveryMappingScenario> MappingScenarios { get; init; } = Array.Empty<EquationDiscoveryMappingScenario>();
    public IReadOnlyList<EquationDiscoveryStageProfile> StageProfiles { get; init; } = Array.Empty<EquationDiscoveryStageProfile>();
    public IReadOnlyList<EquationCandidateResult> EquationFamily { get; init; } = Array.Empty<EquationCandidateResult>();
    public IReadOnlyList<EquationDiscoveryCurve> ObservedProfiles { get; init; } = Array.Empty<EquationDiscoveryCurve>();
    public IReadOnlyList<EquationDiscoveryCurve> ReconstructedProfiles { get; init; } = Array.Empty<EquationDiscoveryCurve>();
    public IReadOnlyList<EquationDiscoveryCurve> ProgressionProfiles { get; init; } = Array.Empty<EquationDiscoveryCurve>();
}

public sealed class EquationDiscoveryMappingScenario
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, double> Anchors { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EquationDiscoveryStageProfile
{
    public string Stage { get; init; } = string.Empty;
    public double Tau { get; init; }
    public int SampleCount { get; init; }
    public double MeanHeightNm { get; init; }
    public double HeightStdNm { get; init; }
    public double MeanWidthNm { get; init; }
    public double WidthStdNm { get; init; }
    public double MeanArea { get; init; }
    public double MeanRoughnessNm { get; init; }
}

public sealed class EquationTermExplanation
{
    public string Symbol { get; init; } = string.Empty;
    public string Meaning { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class EquationCandidateResult
{
    public int Rank { get; init; }
    public string Equation { get; init; } = string.Empty;
    public IReadOnlyList<string> ActiveTerms { get; init; } = Array.Empty<string>();
    public Dictionary<string, double> Coefficients { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EquationCoefficientStatistics> CoefficientStatistics { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double Rmse { get; init; }
    public double PeakHeightError { get; init; }
    public double WidthError { get; init; }
    public double AreaError { get; init; }
    public double CompromiseConsistency { get; init; }
    public double StabilityScore { get; init; }
    public double ComplexityPenalty { get; init; }
    public double Confidence { get; init; }
    public double PseudotimeSensitivity { get; init; }
    public double BootstrapSupport { get; init; }
    public double MetaPriorScore { get; init; }
    public string Notes { get; init; } = string.Empty;

    public string EquationPrimaryLine
    {
        get
        {
            var lines = SplitEquationLines();
            return lines.Length == 0 ? Equation : lines[0];
        }
    }

    public IReadOnlyList<string> EquationSecondaryLines
    {
        get
        {
            var lines = SplitEquationLines();
            return lines.Length <= 1 ? Array.Empty<string>() : lines.Skip(1).ToArray();
        }
    }

    public string ActiveTermsText => ActiveTerms.Count == 0 ? string.Empty : string.Join(" | ", ActiveTerms);

    private string[] SplitEquationLines() =>
        Equation
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class EquationCoefficientStatistics
{
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public double Lower95 { get; init; }
    public double Upper95 { get; init; }
}

public sealed class EquationDiscoveryCurve
{
    public string Label { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public double Tau { get; init; }
    public IReadOnlyList<EquationDiscoveryPoint> Points { get; init; } = Array.Empty<EquationDiscoveryPoint>();
}

public sealed class EquationDiscoveryPoint
{
    public double X { get; init; }
    public double Y { get; init; }

    public PlotPoint ToPlotPoint() => new(X, Y);
}
