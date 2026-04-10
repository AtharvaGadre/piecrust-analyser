using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PiecrustAnalyser.CSharp.Models;

public readonly record struct PointD(double X, double Y);
public readonly record struct PlotPoint(double X, double Y);
public readonly record struct PolylineSeries(IReadOnlyList<PlotPoint> Points, string Color, double Thickness = 1.5, double Opacity = 1.0, bool Dashed = false);

public sealed class DistributionSummary
{
    public double Min { get; init; }
    public double Q1 { get; init; }
    public double Median { get; init; }
    public double Q3 { get; init; }
    public double Max { get; init; }
    public double WhiskerLow { get; init; }
    public double WhiskerHigh { get; init; }
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public double StandardError { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<double> Outliers { get; init; } = Array.Empty<double>();
}

public sealed class BoxPlotDataset
{
    public string Label { get; init; } = string.Empty;
    public DistributionSummary Stats { get; init; } = new();
    public string Color { get; init; } = "#c17832";
    public double MeanMarker { get; init; } = double.NaN;
    public double MeanError { get; init; }
}

public sealed class StageSummaryRow
{
    public string Stage { get; init; } = string.Empty;
    public int ImageCount { get; init; }
    public double HeightMeanNm { get; init; }
    public double HeightStdNm { get; init; }
    public double WidthMeanNm { get; init; }
    public double WidthStdNm { get; init; }
}

public sealed class GuidedMetric
{
    public double ArcNm { get; init; }
    public double WidthNm { get; init; }
    public double HeightNm { get; init; }
    public bool Valid { get; init; }
}

public sealed class GuidedSummary
{
    public int ProfileCount { get; init; }
    public int ValidProfileCount { get; init; }
    public double Continuity { get; init; }
    public double GuideLengthNm { get; init; }
    public double MeanWidthNm { get; init; }
    public double MeanHeightNm { get; init; }
    public double WidthStdNm { get; init; }
    public double HeightStdNm { get; init; }
    public double WidthSemNm { get; init; }
    public double HeightSemNm { get; init; }
    public double RoughnessNm { get; init; }
    public double CurvatureMean { get; init; }
    public double PeakSeparationNm { get; init; }
    public double DipDepthNm { get; init; }
    public double BimodalWeight { get; init; }
    public DistributionSummary? WidthSummary { get; init; }
    public DistributionSummary? HeightSummary { get; init; }
}

public sealed class GrowthQuantificationRow
{
    public string FileName { get; init; } = string.Empty;
    public string Stage { get; init; } = "middle";
    public string ConditionType { get; init; } = "unassigned";
    public double DoseUgPerMl { get; init; }
    public double AdditionRateNm { get; init; }
    public double RemovalRateNm { get; init; }
    public double RawCompromiseRatio { get; init; }
    public double CompromiseRatio { get; init; }
    public double ControlProfileDeviation { get; init; }
    public int ControlReferenceCount { get; init; }
    public string CompromiseDisplayText { get; init; } = "Compromise -";
    public double MeanHeightNm { get; init; }
    public double MeanWidthNm { get; init; }
    public double HeightSemNm { get; init; }
    public double WidthSemNm { get; init; }
}

public sealed class SimulationReferenceInfo
{
    public string FileName { get; init; } = string.Empty;
    public string Stage { get; init; } = "middle";
    public int SequenceOrder { get; init; }
    public double Position01 { get; init; }
}

public sealed class SurfaceSimulationResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double ScanSizeNmX { get; init; }
    public double ScanSizeNmY { get; init; }
    public string Unit { get; init; } = "nm";
    public int PolynomialDegree { get; init; }
    public IReadOnlyList<SimulationReferenceInfo> References { get; init; } = Array.Empty<SimulationReferenceInfo>();
    public IReadOnlyList<double[]> ReferenceSurfaces { get; init; } = Array.Empty<double[]>();
    public IReadOnlyList<double> FrameProgresses { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double[]> Frames { get; init; } = Array.Empty<double[]>();
    public double DisplayMin { get; init; }
    public double DisplayMax { get; init; }
    public bool UsesGuidedAlignment { get; init; }
}

public sealed class EvolutionRecord
{
    public string FileName { get; init; } = string.Empty;
    public string Stage { get; init; } = "middle";
    public double[] Profile { get; init; } = Array.Empty<double>();
    public double[] GaussianParameters { get; init; } = Array.Empty<double>();
}

public sealed class EvolutionStageBucket
{
    public string Stage { get; init; } = string.Empty;
    public List<EvolutionRecord> Records { get; } = new();
    public double[]? MeanProfile { get; set; }
    public double[]? MeanParameters { get; set; }
}

public sealed class LoadedHeightMap
{
    public string Name { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Unit { get; init; } = "nm";
    public string ChannelDisplay { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public double ScanSizeNm { get; init; }
    public double NmPerPixel { get; init; }
    public double[] Data { get; init; } = Array.Empty<double>();
    public bool PreferScientificPreview { get; init; }
}

public sealed partial class PiecrustFileState : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string filePath = string.Empty;
    [ObservableProperty] private string format = string.Empty;
    [ObservableProperty] private string stage = "early";
    [ObservableProperty] private string conditionType = "unassigned";
    [ObservableProperty] private string unit = "nm";
    [ObservableProperty] private int pixelWidth;
    [ObservableProperty] private int pixelHeight;
    [ObservableProperty] private double scanSizeNm = 500;
    [ObservableProperty] private double nmPerPixel = 1;
    [ObservableProperty] private double displayMin;
    [ObservableProperty] private double displayMax = 1;
    [ObservableProperty] private int sequenceOrder = 1;
    [ObservableProperty] private double guideCorridorWidthNm = 20;
    [ObservableProperty] private double antibioticDoseUgPerMl;
    [ObservableProperty] private bool useManualGuide = true;
    [ObservableProperty] private bool guideLineFinished;
    [ObservableProperty] private WriteableBitmap? previewBitmap;
    [ObservableProperty] private GuidedSummary? guidedSummary;

    public double[] HeightData { get; init; } = Array.Empty<double>();
    public ObservableCollection<PointD> ProfileLine { get; } = new();
    public ObservableCollection<PointD> GuidePoints { get; } = new();
    public ObservableCollection<PlotPoint> ProfileSeries { get; } = new();
    public ObservableCollection<GuidedMetric> GuidedMetrics { get; } = new();
    public EvolutionRecord? EvolutionRecord { get; set; }
}
