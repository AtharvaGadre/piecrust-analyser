using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PiecrustAnalyser.CSharp.Models;
using PiecrustAnalyser.CSharp.Services;

namespace PiecrustAnalyser.CSharp.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly FileLoadingService _fileLoading = new();
    private readonly PreviewBitmapService _previewBitmapService = new();
    private readonly HeightMapDisplayService _heightMapDisplay = new();
    private readonly PiecrustAnalysisService _analysis = new();
    private readonly SupervisedGrowthLearningService _supervisedGrowthLearning = new();
    private readonly EquationDiscoveryService _equationDiscovery = new();
    private readonly SessionPersistenceService _sessionPersistence = new();
    private readonly DispatcherTimer _simulationTimer;
    private SurfaceSimulationResult? _surfaceSimulationCache;
    private SupervisedGrowthModel? _supervisedGrowthModel;
    private EquationDiscoveryResult? _equationDiscoveryResult;
    private IReadOnlyList<SimulationEquationCandidate> _simulationEquationCandidates = Array.Empty<SimulationEquationCandidate>();
    private bool _suspendSessionPersistence;
    private bool _syncingSelectedDisplayControls;

    public IReadOnlyList<string> ConditionOptions { get; } = new[] { "unassigned", "control", "treated" };
    public IReadOnlyList<string> StageOptions { get; } = new[] { "early", "middle", "late" };
    public IReadOnlyList<string> DisplayModeOptions { get; } = new[] { "auto", "full", "fixed" };
    public ObservableCollection<PiecrustFileState> Files { get; } = new();

    [ObservableProperty] private PiecrustFileState? selectedFile;
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private bool isMarkingProfileLine;
    [ObservableProperty] private bool isGuideDrawing;
    [ObservableProperty] private double evolutionProgress = 0.55;
    [ObservableProperty] private string statusText = "Load TIFF/SPM files to begin.";
    [ObservableProperty] private double currentCorridorHalfWidthPixels = 8;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> currentProfileSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private IReadOnlyList<PolylineSeries> evolutionSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private IReadOnlyList<BoxPlotDataset> heightBoxPlots = Array.Empty<BoxPlotDataset>();
    [ObservableProperty] private IReadOnlyList<BoxPlotDataset> widthBoxPlots = Array.Empty<BoxPlotDataset>();
    [ObservableProperty] private IReadOnlyList<BoxPlotDataset> heightWidthRatioBoxPlots = Array.Empty<BoxPlotDataset>();
    [ObservableProperty] private IReadOnlyList<StageSummaryRow> stageSummaries = Array.Empty<StageSummaryRow>();
    [ObservableProperty] private IReadOnlyList<GrowthQuantificationRow> growthRows = Array.Empty<GrowthQuantificationRow>();
    [ObservableProperty] private GrowthQuantificationRow? currentGrowthRow;
    [ObservableProperty] private string selectedFileNameText = "No file selected";
    [ObservableProperty] private string selectedChannelDisplayText = "Channel: -";
    [ObservableProperty] private string selectedDisplayMinText = "Display Min: -";
    [ObservableProperty] private string selectedDisplayMaxText = "Display Max: -";
    [ObservableProperty] private string selectedDisplayReferenceText = "Display Reference: -";
    [ObservableProperty] private string selectedEstimatedNoiseText = "Estimated Noise Sigma: -";
    [ObservableProperty] private string selectedMeanHeightText = "Mean Height: run guided extraction";
    [ObservableProperty] private string selectedMeanWidthText = "Mean Width: run guided extraction";
    [ObservableProperty] private string selectedHeightWidthRatioText = "Height/Width Ratio: -";
    [ObservableProperty] private string selectedContinuityText = "Continuity: -";
    [ObservableProperty] private string selectedPeakSeparationText = "Peak Separation: -";
    [ObservableProperty] private string selectedDisplayRangeMode = "auto";
    [ObservableProperty] private double selectedDisplayRangeMin;
    [ObservableProperty] private double selectedDisplayRangeMax = 1;
    [ObservableProperty] private double selectedDisplayBoundsMin;
    [ObservableProperty] private double selectedDisplayBoundsMax = 1;
    [ObservableProperty] private double selectedDisplaySliderStep = 0.1;
    [ObservableProperty] private IReadOnlyList<double> selectedDisplayHistogram = Array.Empty<double>();
    [ObservableProperty] private double selectedDisplayWindowStartPercent;
    [ObservableProperty] private double selectedDisplayWindowEndPercent = 100;
    [ObservableProperty] private string currentAdditionRateText = "Addition Rate: -";
    [ObservableProperty] private string currentRemovalRateText = "Removal Rate: -";
    [ObservableProperty] private string currentCompromiseText = "Compromise: -";
    [ObservableProperty] private string compromiseMethodText = "Compromise score uses guided height as addition and guided width + roughness as removal. Untreated controls act as the biological reference group.";
    [ObservableProperty] private string currentProfileXAxisLabel = "x [nm]";
    [ObservableProperty] private string currentProfileYAxisLabel = "y [nm]";
    [ObservableProperty] private string evolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
    [ObservableProperty] private string evolutionYAxisLabel = "Baseline-shifted height [nm]";
    [ObservableProperty] private string selectedStageHintText = "Stage is auto-classified on load, but you can change it manually.";
    [ObservableProperty] private string currentCorridorWidthOverlayText = "Corridor: -";
    [ObservableProperty] private PiecrustFileState? simulationStartFile;
    [ObservableProperty] private PiecrustFileState? simulationEndFile;
    [ObservableProperty] private double simulationProgress;
    [ObservableProperty] private bool isSimulationPlaying;
    [ObservableProperty] private string simulationXAxisLabel = "Aligned x [nm]";
    [ObservableProperty] private string simulationYAxisLabel = "Simulated height [nm]";
    [ObservableProperty] private string simulationStatusText = "Select start and end reference files to run the full 2D growth simulation.";
    [ObservableProperty] private string supervisedModelStatusText = "Supervised ML status: no learned examples yet.";
    [ObservableProperty] private string simulationReferenceSummaryText = "Ordered references: -";
    [ObservableProperty] private string simulationSurfaceMetaText = "Surface frame: -";
    [ObservableProperty] private string simulationSurfaceXAxisLabel = "Aligned x [nm]";
    [ObservableProperty] private string simulationSurfaceYAxisLabel = "Aligned y [nm]";
    [ObservableProperty] private WriteableBitmap? simulationSurfaceBitmap;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> simulationSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private string simulationPlotLegendText = "Dotted = evolving cross-section | Solid = bimodal Gaussian fit";
    [ObservableProperty] private double simulationPlotFixedYMin = double.NaN;
    [ObservableProperty] private double simulationPlotFixedYMax = double.NaN;
    [ObservableProperty] private string equationDiscoveryStatusText = "Use guided, stage-labelled profiles to discover a family of pseudo-time progression equations.";
    [ObservableProperty] private string equationDiscoveryMetaText = "Pseudo-time tau is an ordered latent progression variable derived from stage labels, not real clock time.";
    [ObservableProperty] private string equationDiscoveryStageMappingText = "Stage anchors: early = 0.00, middle = 0.50, late = 1.00";
    [ObservableProperty] private string equationDiscoveryProfileModeText = "Current reduced model: z(s, tau), where s is aligned centreline position extracted from the original x-y-z AFM surface.";
    [ObservableProperty] private string equationDiscoveryOverlayLegendText = "Stage colours: Early = green, Middle = amber, Late = red. Solid lines = observed AFM stage profiles z(s). Dashed lines = reconstructed z(s, tau) profiles from the top-ranked candidate at the matching stage anchors.";
    [ObservableProperty] private string equationDiscoveryProgressionLegendText = "Reconstructed pseudo-time progression of z(s, tau): the colours move from green near Early (tau≈0), through amber near Middle, toward red near Late (tau≈1).";
    [ObservableProperty] private string equationDiscoveryTermGuideText = "Term guide: z = AFM height, s = aligned centreline position from x-y-z AFM data, dz/ds = slope, d2z/ds2 = curvature, d3z/ds3 and d4z/ds4 = higher-order shape change, tau = latent growth progression rather than real time.";
    [ObservableProperty] private string equationDiscoveryXAxisLabel = "Aligned centreline position s [nm]";
    [ObservableProperty] private string equationDiscoveryYAxisLabel = "Height z [nm]";
    [ObservableProperty] private IReadOnlyList<EquationDiscoveryStageProfile> equationDiscoveryStageProfiles = Array.Empty<EquationDiscoveryStageProfile>();
    [ObservableProperty] private IReadOnlyList<EquationTermExplanation> equationTermExplanations = Array.Empty<EquationTermExplanation>();
    [ObservableProperty] private IReadOnlyList<EquationCandidateResult> equationFamily = Array.Empty<EquationCandidateResult>();
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationOverlaySeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationProgressionSeries = Array.Empty<PolylineSeries>();

    public MainWindowViewModel()
    {
        _simulationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _simulationTimer.Tick += OnSimulationTick;
    }

    private sealed class SimulationEquationCandidate
    {
        public required EquationCandidateResult Display { get; init; }
        public required int Degree { get; init; }
        public required double[] LeftAmplitudeCoefficients { get; init; }
        public required double[] LeftSigmaCoefficients { get; init; }
        public required double[] RightAmplitudeCoefficients { get; init; }
        public required double[] RightSigmaCoefficients { get; init; }
        public required double[] SeparationCoefficients { get; init; }
    }

    public async Task InitializeAsync()
    {
        var snapshot = _sessionPersistence.Load();
        if (snapshot is null || snapshot.Files.Count == 0) return;

        try
        {
            _suspendSessionPersistence = true;
            var existingPaths = snapshot.Files
                .Select(file => file.FilePath)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (existingPaths.Length == 0) return;

            await LoadFilesAsync(existingPaths);

            foreach (var file in Files)
            {
                var saved = snapshot.Files.FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase));
                if (saved is null) continue;

                file.Stage = saved.Stage;
                file.ConditionType = saved.ConditionType;
                file.AntibioticDoseUgPerMl = saved.AntibioticDoseUgPerMl;
                file.SequenceOrder = saved.SequenceOrder;
                file.GuideCorridorWidthNm = saved.GuideCorridorWidthNm;
                file.DisplayRangeMode = saved.DisplayRangeMode;
                file.FixedDisplayMin = saved.FixedDisplayMin;
                file.FixedDisplayMax = saved.FixedDisplayMax;
                file.GuideLineFinished = saved.GuideLineFinished;
                file.GuidePoints.Clear();
                foreach (var point in saved.GuidePoints) file.GuidePoints.Add(point.ToPointD());
                file.ProfileLine.Clear();
                foreach (var point in saved.ProfileLine) file.ProfileLine.Add(point.ToPointD());
                ApplyDisplaySettingsToFile(file);
                if (file.GuideLineFinished && file.GuidePoints.Count >= 2)
                {
                    file.GuidedSummary = _analysis.ExtractGuidedSummary(file);
                }
            }

            SelectedTabIndex = snapshot.SelectedTabIndex;
            EvolutionProgress = snapshot.EvolutionProgress;
            SimulationProgress = snapshot.SimulationProgress;
            SelectedFile = Files.FirstOrDefault(file =>
                string.Equals(file.FilePath, snapshot.SelectedFilePath, StringComparison.OrdinalIgnoreCase))
                ?? Files.FirstOrDefault();
            SimulationStartFile = Files.FirstOrDefault(file =>
                string.Equals(file.FilePath, snapshot.SimulationStartFilePath, StringComparison.OrdinalIgnoreCase));
            SimulationEndFile = Files.FirstOrDefault(file =>
                string.Equals(file.FilePath, snapshot.SimulationEndFilePath, StringComparison.OrdinalIgnoreCase));

            RefreshDerivedState();
            StatusText = $"Restored {Files.Count} file(s) from the previous session.";
        }
        finally
        {
            _suspendSessionPersistence = false;
            PersistSessionIfPossible();
        }
    }

    public async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        ClearEquationDiscoveryResults();
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                StatusText = $"Loading {Path.GetFileName(path)}...";
                var loaded = await _fileLoading.LoadAsync(path, 500).ConfigureAwait(true);
                if (loaded is null)
                {
                    StatusText = $"Skipped unreadable file: {Path.GetFileName(path)}";
                    continue;
                }
                string stage;
                try
                {
                    stage = _analysis.ClassifyStage(loaded.Data);
                }
                catch
                {
                    stage = "early";
                }

                var file = new PiecrustFileState
                {
                    Name = loaded.Name,
                    FilePath = loaded.FilePath,
                    Format = loaded.Format,
                    ChannelDisplay = loaded.ChannelDisplay,
                    PixelWidth = loaded.Width,
                    PixelHeight = loaded.Height,
                    ScanSizeNm = loaded.ScanSizeNm,
                    NmPerPixel = loaded.NmPerPixel,
                    Unit = loaded.Unit,
                    DisplayRangeMode = loaded.DefaultDisplayRangeMode,
                    DisplayRangeFullMin = loaded.DisplayRangeFullMin,
                    DisplayRangeFullMax = loaded.DisplayRangeFullMax,
                    DisplayRangeAutoMin = loaded.DisplayRangeAutoMin,
                    DisplayRangeAutoMax = loaded.DisplayRangeAutoMax,
                    DisplayRangeSuggestedMin = loaded.DisplayRangeSuggestedMin,
                    DisplayRangeSuggestedMax = loaded.DisplayRangeSuggestedMax,
                    FixedDisplayMin = loaded.DisplayRangeSuggestedMin,
                    FixedDisplayMax = loaded.DisplayRangeSuggestedMax,
                    DisplayReferenceNm = loaded.DisplayReferenceNm,
                    EstimatedNoiseSigma = loaded.EstimatedNoiseSigma,
                    Stage = stage,
                    SequenceOrder = Files.Count + 1,
                    HeightData = loaded.Data,
                    RawHeightData = loaded.RawData,
                    DisplayHeightData = loaded.DisplayData,
                    GuideCorridorWidthNm = Math.Max(8, loaded.NmPerPixel * 12)
                };
                ApplyDisplaySettingsToFile(file);
                try
                {
                    file.EvolutionRecord = _analysis.BuildEvolutionRecord(file);
                }
                catch
                {
                    file.EvolutionRecord = null;
                }
                file.PropertyChanged += OnFileStateChanged;
                Files.Add(file);
                SelectedFile ??= file;
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        InvalidateSimulationCache();
        RefreshDerivedState();
        StatusText = Files.Count == 0 ? "No readable files were loaded." : $"Loaded {Files.Count} file(s).";
        PersistSessionIfPossible();
    }

    public void RemoveSelectedFile()
    {
        if (SelectedFile is null) return;
        ClearEquationDiscoveryResults();
        var idx = Files.IndexOf(SelectedFile);
        SelectedFile.PropertyChanged -= OnFileStateChanged;
        Files.Remove(SelectedFile);
        SelectedFile = Files.Count == 0 ? null : Files[Math.Clamp(idx, 0, Files.Count - 1)];
        InvalidateSimulationCache();
        RefreshDerivedState();
        PersistSessionIfPossible();
    }

    public void AutoNumberFiles()
    {
        ClearEquationDiscoveryResults();
        for (var i = 0; i < Files.Count; i++)
        {
            Files[i].SequenceOrder = i + 1;
        }

        InvalidateSimulationCache();
        RefreshDerivedState();
        StatusText = Files.Count == 0 ? "No files are loaded to number." : "Sequence numbers refreshed from the current file list order.";
        PersistSessionIfPossible();
    }

    public void BeginProfileLineSelection()
    {
        if (SelectedFile is null) return;
        IsGuideDrawing = false;
        IsMarkingProfileLine = true;
        SelectedFile.ProfileLine.Clear();
        StatusText = "Click two points on the image to define the line profile.";
        PersistSessionIfPossible();
    }

    public void ClearProfileLine()
    {
        if (SelectedFile is null) return;
        SelectedFile.ProfileLine.Clear();
        SelectedFile.ProfileSeries.Clear();
        IsMarkingProfileLine = false;
        RefreshCurrentProfile();
        PersistSessionIfPossible();
    }

    public void StartGuideLine()
    {
        if (SelectedFile is null) return;
        IsMarkingProfileLine = false;
        IsGuideDrawing = true;
        SelectedFile.GuidePoints.Clear();
        SelectedFile.GuideLineFinished = false;
        StatusText = "Click along the piecrust centre line, then press Finish Centre Line.";
        PersistSessionIfPossible();
    }

    public void FinishGuideLine()
    {
        if (SelectedFile is null) return;
        SelectedFile.GuideLineFinished = SelectedFile.GuidePoints.Count >= 2;
        IsGuideDrawing = false;
        StatusText = SelectedFile.GuideLineFinished ? "Centre line finished. Run guided extraction next." : "Add at least two guide points first.";
        PersistSessionIfPossible();
    }

    public void UndoGuidePoint()
    {
        if (SelectedFile is null || SelectedFile.GuidePoints.Count == 0) return;
        SelectedFile.GuidePoints.RemoveAt(SelectedFile.GuidePoints.Count - 1);
        SelectedFile.GuideLineFinished = false;
        PersistSessionIfPossible();
    }

    public void ClearGuideLine()
    {
        if (SelectedFile is null) return;
        ClearEquationDiscoveryResults();
        SelectedFile.GuidePoints.Clear();
        SelectedFile.GuidedMetrics.Clear();
        SelectedFile.GuidedSummary = null;
        SelectedFile.GuideLineFinished = false;
        IsGuideDrawing = false;
        RefreshDerivedState();
        PersistSessionIfPossible();
    }

    public void HandleCanvasClick(PointD point)
    {
        if (SelectedFile is null) return;
        if (IsMarkingProfileLine)
        {
            SelectedFile.ProfileLine.Add(point);
            if (SelectedFile.ProfileLine.Count >= 2)
            {
                while (SelectedFile.ProfileLine.Count > 2) SelectedFile.ProfileLine.RemoveAt(0);
                IsMarkingProfileLine = false;
                RefreshCurrentProfile();
                StatusText = "Line profile updated.";
                PersistSessionIfPossible();
            }
            return;
        }

        if (IsGuideDrawing)
        {
            SelectedFile.GuidePoints.Add(point);
            StatusText = $"Guide points: {SelectedFile.GuidePoints.Count}";
            PersistSessionIfPossible();
        }
    }

    public void RunGuidedExtraction()
    {
        if (SelectedFile is null) return;
        ClearEquationDiscoveryResults();
        SyncSelectedFileDisplayMetrics();
        SelectedFile.GuidedSummary = _analysis.ExtractGuidedSummary(SelectedFile);
        RefreshDerivedState();
        StatusText = SelectedFile.GuidedSummary is null
            ? "Guided extraction needs a finished centre line."
            : $"Guided extraction complete for {SelectedFile.Name}.";
        PersistSessionIfPossible();
    }

    public string BuildCurrentResultsCsv()
    {
        if (SelectedFile is null) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("file,stage,condition,dose_ug_per_ml,channel,display_mode,display_min_nm,display_max_nm,display_reference_nm,estimated_noise_sigma_nm,mean_height_nm,height_sem_nm,mean_width_nm,width_sem_nm,height_to_width_ratio,continuity,roughness_nm,peak_separation_nm,dip_depth_nm");
        if (SelectedFile.GuidedSummary is { } summary)
        {
            sb.AppendLine(string.Join(",",
                Csv(SelectedFile.Name),
                Csv(SelectedFile.Stage),
                Csv(SelectedFile.ConditionType),
                SelectedFile.AntibioticDoseUgPerMl.ToString("F4"),
                Csv(SelectedFile.ChannelDisplay),
                Csv(SelectedFile.DisplayRangeMode),
                SelectedFile.DisplayMin.ToString("F4"),
                SelectedFile.DisplayMax.ToString("F4"),
                SelectedFile.DisplayReferenceNm.ToString("F4"),
                SelectedFile.EstimatedNoiseSigma.ToString("F4"),
                summary.MeanHeightNm.ToString("F4"),
                summary.HeightSemNm.ToString("F4"),
                summary.MeanWidthNm.ToString("F4"),
                summary.WidthSemNm.ToString("F4"),
                summary.HeightToWidthRatio.ToString("F5"),
                summary.Continuity.ToString("F4"),
                summary.RoughnessNm.ToString("F4"),
                summary.PeakSeparationNm.ToString("F4"),
                summary.DipDepthNm.ToString("F4")));
        }
        sb.AppendLine();
        sb.AppendLine("arc_nm,width_nm,height_nm,valid");
        foreach (var metric in SelectedFile.GuidedMetrics)
        {
            sb.AppendLine($"{metric.ArcNm:F4},{metric.WidthNm:F4},{metric.HeightNm:F4},{metric.Valid}");
        }
        return sb.ToString();
    }

    public string BuildCurrentLineProfileCsv()
    {
        if (SelectedFile is null || CurrentProfileSeries.Count == 0 || CurrentProfileSeries[0].Points.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"file,{Csv(SelectedFile.Name)}");
        sb.AppendLine($"x_unit,{Csv(SelectedFile.Unit)}");
        sb.AppendLine($"y_unit,{Csv(SelectedFile.Unit)}");
        sb.AppendLine("x,y");
        foreach (var point in CurrentProfileSeries[0].Points)
        {
            sb.AppendLine($"{point.X:F4},{point.Y:F4}");
        }
        return sb.ToString();
    }

    public string BuildStageBoxPlotsCsv()
    {
        var sb = new StringBuilder();
        if (StageSummaries.Count > 0)
        {
            sb.AppendLine("stage,image_count,height_mean_nm,height_std_nm,width_mean_nm,width_std_nm,height_to_width_ratio_mean,height_to_width_ratio_std");
            foreach (var row in StageSummaries)
            {
                sb.AppendLine(string.Join(",",
                    Csv(row.Stage),
                    row.ImageCount.ToString(CultureInfo.InvariantCulture),
                    row.HeightMeanNm.ToString("F4"),
                    row.HeightStdNm.ToString("F4"),
                    row.WidthMeanNm.ToString("F4"),
                    row.WidthStdNm.ToString("F4"),
                    row.HeightWidthRatioMean.ToString("F5"),
                    row.HeightWidthRatioStd.ToString("F5")));
            }

            sb.AppendLine();
        }
        sb.AppendLine("measurement,sequence,stage,file_name,count,mean,median,q1,q3,whisker_low,whisker_high,sem,stddev");
        foreach (var dataset in HeightBoxPlots)
        {
            AppendBoxPlotRow(sb, "height_nm", dataset);
        }
        foreach (var dataset in WidthBoxPlots)
        {
            AppendBoxPlotRow(sb, "width_nm", dataset);
        }
        foreach (var dataset in HeightWidthRatioBoxPlots)
        {
            AppendBoxPlotRow(sb, "height_to_width_ratio", dataset);
        }
        return sb.ToString();
    }

    public string BuildGrowthQuantificationCsv()
    {
        if (GrowthRows.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("file,stage,condition,dose_ug_per_ml,mean_height_nm,height_sem_nm,mean_width_nm,width_sem_nm,height_to_width_ratio,addition_rate_nm,removal_rate_nm,raw_compromise_ratio,compromise_ratio,control_profile_deviation,control_reference_count");
        foreach (var row in GrowthRows)
        {
            sb.AppendLine(string.Join(",",
                Csv(row.FileName),
                Csv(row.Stage),
                Csv(row.ConditionType),
                row.DoseUgPerMl.ToString("F4"),
                row.MeanHeightNm.ToString("F4"),
                row.HeightSemNm.ToString("F4"),
                row.MeanWidthNm.ToString("F4"),
                row.WidthSemNm.ToString("F4"),
                row.HeightToWidthRatio.ToString("F5"),
                row.AdditionRateNm.ToString("F4"),
                row.RemovalRateNm.ToString("F4"),
                row.RawCompromiseRatio.ToString("F5"),
                row.CompromiseRatio.ToString("F5"),
                row.ControlProfileDeviation.ToString("F5"),
                row.ControlReferenceCount.ToString(CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    public string BuildGrowthModelCsv()
    {
        if (SimulationStartFile is null || SimulationEndFile is null) return string.Empty;
        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || simulation.Frames.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"start_file,{Csv(SimulationStartFile.Name)}");
        sb.AppendLine($"end_file,{Csv(SimulationEndFile.Name)}");
        sb.AppendLine($"x_unit,{Csv(simulation.Unit)}");
        sb.AppendLine($"y_unit,{Csv(simulation.Unit)}");
        sb.AppendLine($"z_unit,{Csv(simulation.Unit)}");
        sb.AppendLine($"polynomial_degree,{simulation.PolynomialDegree.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"supervised_learning,{simulation.UsesSupervisedLearning}");
        sb.AppendLine($"supervised_example_count,{simulation.SupervisedExampleCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"supervised_blend_weight,{simulation.SupervisedBlendWeight.ToString("F4", CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("reference_index,sequence_order,stage,position_01,file");
        for (var i = 0; i < simulation.References.Count; i++)
        {
            var reference = simulation.References[i];
            sb.AppendLine($"{i},{reference.SequenceOrder.ToString(CultureInfo.InvariantCulture)},{Csv(reference.Stage)},{reference.Position01.ToString("F4", CultureInfo.InvariantCulture)},{Csv(reference.FileName)}");
        }

        sb.AppendLine();
        sb.AppendLine("frame_index,progress,x_nm,y_nm,height_nm");
        for (var frameIndex = 0; frameIndex < simulation.Frames.Count; frameIndex++)
        {
            var frame = simulation.Frames[frameIndex];
            var progress = simulation.FrameProgresses[frameIndex];
            for (var y = 0; y < simulation.Height; y++)
            {
                var yNm = simulation.Height > 1 ? y * simulation.ScanSizeNmY / (simulation.Height - 1.0) : 0;
                for (var x = 0; x < simulation.Width; x++)
                {
                    var xNm = simulation.Width > 1 ? x * simulation.ScanSizeNmX / (simulation.Width - 1.0) : 0;
                    var height = frame[y * simulation.Width + x];
                    sb.AppendLine($"{frameIndex},{progress.ToString("F4", CultureInfo.InvariantCulture)},{xNm.ToString("F4", CultureInfo.InvariantCulture)},{yNm.ToString("F4", CultureInfo.InvariantCulture)},{height.ToString("F4", CultureInfo.InvariantCulture)}");
                }
            }
        }
        return sb.ToString();
    }

    public async Task DiscoverGrowthEquationsAsync()
    {
        var profileInputs = Files
            .Select(file => _analysis.BuildEquationDiscoveryProfileInput(file))
            .Where(input => input is not null)
            .Cast<EquationDiscoveryProfileInput>()
            .Where(input => !string.IsNullOrWhiteSpace(input.Stage))
            .ToArray();

        var stageCount = profileInputs
            .Select(input => input.Stage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (profileInputs.Length < 2 || stageCount < 2)
        {
            ClearEquationDiscoveryResults("Equation discovery needs at least two guided profiles spanning at least two ordered stages.");
            StatusText = EquationDiscoveryStatusText;
            return;
        }

        try
        {
            EquationDiscoveryStatusText = $"Discovering pseudo-time growth equations from {profileInputs.Length} guided profile(s)...";
            EquationDiscoveryMetaText = "Preparing centreline-aligned AFM profiles, conservative derivatives, sparse candidate libraries, and pseudo-time sensitivity checks.";
            StatusText = EquationDiscoveryStatusText;

            var request = new EquationDiscoveryRequest
            {
                SampleId = $"piecrust-session-{DateTime.UtcNow:yyyyMMddHHmmss}",
                TimeMode = "pseudotime_stage_ordered",
                ProfileMode = "centerline_arc_length_profile",
                StageMapping = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["early"] = 0.0,
                    ["middle"] = 0.5,
                    ["late"] = 1.0
                },
                Options = new EquationDiscoveryOptions
                {
                    SpatialGridCount = 220,
                    BootstrapCount = Math.Clamp(profileInputs.Length * 6, 18, 36),
                    StageJitter = 0.10,
                    SampleSpacingNm = 1.0,
                    DerivativeMode = "savitzky_golay",
                    SparseBackend = "stlsq"
                },
                Files = profileInputs
            };

            var result = await _equationDiscovery.DiscoverAsync(request).ConfigureAwait(true);
            if (result is null)
            {
                ClearEquationDiscoveryResults("Equation discovery did not return a result for the current guided stage set.");
                StatusText = EquationDiscoveryStatusText;
                return;
            }

            ApplyEquationDiscoveryResult(result);
            ApplySimulationAlignedEquationFamilyIfAvailable();
            StatusText = $"Equation discovery complete. Ranked {EquationFamily.Count} pseudo-time candidate equation(s).";
        }
        catch (Exception ex)
        {
            ClearEquationDiscoveryResults($"Equation discovery failed: {ex.Message}");
            StatusText = EquationDiscoveryStatusText;
        }
    }

    public string BuildEquationDiscoveryJson()
    {
        return _equationDiscoveryResult?.RawJson ?? string.Empty;
    }

    public string BuildEquationDiscoveryCsv()
    {
        if (_equationDiscoveryResult is null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"sample_id,{Csv(_equationDiscoveryResult.SampleId)}");
        sb.AppendLine($"time_mode,{Csv(_equationDiscoveryResult.TimeMode)}");
        sb.AppendLine($"profile_mode,{Csv(_equationDiscoveryResult.ProfileMode)}");
        sb.AppendLine($"stage_mapping_mode,{Csv(_equationDiscoveryResult.StageMappingMode)}");
        sb.AppendLine($"spatial_coordinate_label,{Csv(_equationDiscoveryResult.SpatialCoordinateLabel)}");
        sb.AppendLine($"height_label,{Csv(_equationDiscoveryResult.HeightLabel)}");
        sb.AppendLine();

        sb.AppendLine("stage,tau");
        foreach (var entry in _equationDiscoveryResult.StageMapping.OrderBy(entry => entry.Value))
        {
            sb.AppendLine($"{Csv(entry.Key)},{entry.Value.ToString("F4", CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine();
        sb.AppendLine("rank,equation,rmse,peak_height_error,width_error,area_error,compromise_consistency,stability_score,complexity_penalty,confidence,pseudotime_sensitivity,bootstrap_support,meta_prior_score,notes");
        foreach (var candidate in EquationFamily)
        {
            sb.AppendLine(string.Join(",",
                candidate.Rank.ToString(CultureInfo.InvariantCulture),
                Csv(candidate.Equation),
                candidate.Rmse.ToString("F6", CultureInfo.InvariantCulture),
                candidate.PeakHeightError.ToString("F6", CultureInfo.InvariantCulture),
                candidate.WidthError.ToString("F6", CultureInfo.InvariantCulture),
                candidate.AreaError.ToString("F6", CultureInfo.InvariantCulture),
                candidate.CompromiseConsistency.ToString("F6", CultureInfo.InvariantCulture),
                candidate.StabilityScore.ToString("F6", CultureInfo.InvariantCulture),
                candidate.ComplexityPenalty.ToString("F6", CultureInfo.InvariantCulture),
                candidate.Confidence.ToString("F6", CultureInfo.InvariantCulture),
                candidate.PseudotimeSensitivity.ToString("F6", CultureInfo.InvariantCulture),
                candidate.BootstrapSupport.ToString("F6", CultureInfo.InvariantCulture),
                candidate.MetaPriorScore.ToString("F6", CultureInfo.InvariantCulture),
                Csv(candidate.Notes)));
        }

        sb.AppendLine();
        sb.AppendLine("candidate_rank,term,mean,stddev,lower95,upper95");
        foreach (var candidate in EquationFamily)
        {
            foreach (var term in candidate.CoefficientStatistics.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var stats = term.Value;
                sb.AppendLine(string.Join(",",
                    candidate.Rank.ToString(CultureInfo.InvariantCulture),
                    Csv(term.Key),
                    stats.Mean.ToString("F6", CultureInfo.InvariantCulture),
                    stats.StandardDeviation.ToString("F6", CultureInfo.InvariantCulture),
                    stats.Lower95.ToString("F6", CultureInfo.InvariantCulture),
                    stats.Upper95.ToString("F6", CultureInfo.InvariantCulture)));
            }
        }

        sb.AppendLine();
        sb.AppendLine("stage,tau,sample_count,mean_height_nm,height_std_nm,mean_width_nm,width_std_nm,mean_area,mean_roughness_nm");
        foreach (var stage in _equationDiscoveryResult.StageProfiles)
        {
            sb.AppendLine(string.Join(",",
                Csv(stage.Stage),
                stage.Tau.ToString("F4", CultureInfo.InvariantCulture),
                stage.SampleCount.ToString(CultureInfo.InvariantCulture),
                stage.MeanHeightNm.ToString("F6", CultureInfo.InvariantCulture),
                stage.HeightStdNm.ToString("F6", CultureInfo.InvariantCulture),
                stage.MeanWidthNm.ToString("F6", CultureInfo.InvariantCulture),
                stage.WidthStdNm.ToString("F6", CultureInfo.InvariantCulture),
                stage.MeanArea.ToString("F6", CultureInfo.InvariantCulture),
                stage.MeanRoughnessNm.ToString("F6", CultureInfo.InvariantCulture)));
        }

        AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ObservedProfiles);
        AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ReconstructedProfiles);
        AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ProgressionProfiles);

        return sb.ToString();
    }

    partial void OnSelectedFileChanged(PiecrustFileState? value)
    {
        SyncSelectedFileDisplayMetrics();
        SyncSelectedDisplayControlsFromSelectedFile();
        RefreshCurrentProfile();
        CurrentGrowthRow = value is null ? null : _analysis.BuildGrowthQuantification(Files, value);
        RefreshSelectedSummaryText();
        PersistSessionIfPossible();
    }

    partial void OnEvolutionProgressChanged(double value)
    {
        RefreshEvolutionSeries();
        PersistSessionIfPossible();
    }

    private void RefreshDerivedState()
    {
        SyncSelectedFileDisplayMetrics();
        RefreshCurrentProfile();
        HeightBoxPlots = _analysis.BuildHeightBoxPlots(Files).ToArray();
        WidthBoxPlots = _analysis.BuildWidthBoxPlots(Files).ToArray();
        HeightWidthRatioBoxPlots = _analysis.BuildHeightWidthRatioBoxPlots(Files).ToArray();
        StageSummaries = _analysis.BuildStageSummaries(Files).ToArray();
        GrowthRows = Files.Select(f => _analysis.BuildGrowthQuantification(Files, f)).Where(r => r is not null).Cast<GrowthQuantificationRow>().ToArray();
        CurrentGrowthRow = SelectedFile is null ? null : _analysis.BuildGrowthQuantification(Files, SelectedFile);
        RefreshSupervisedModel();
        RefreshCompromiseMethodText();
        EnsureSimulationReferences();
        RefreshEvolutionSeries();
        RefreshSimulationSeries();
        RefreshSelectedSummaryText();
        PersistSessionIfPossible();
    }

    private void RefreshCurrentProfile()
    {
        if (SelectedFile is null)
        {
            CurrentProfileSeries = Array.Empty<PolylineSeries>();
            return;
        }
        var profile = _analysis.BuildLineProfile(SelectedFile).ToArray();
        SelectedFile.ProfileSeries.Clear();
        foreach (var point in profile) SelectedFile.ProfileSeries.Add(point);
        CurrentProfileSeries = profile.Length == 0
            ? Array.Empty<PolylineSeries>()
            : new[] { new PolylineSeries(profile, "#2f2f2f", 1.7) };
    }

    private void ApplyEquationDiscoveryResult(EquationDiscoveryResult result)
    {
        _equationDiscoveryResult = result;
        EquationDiscoveryStageProfiles = result.StageProfiles
            .OrderBy(stage => stage.Tau)
            .ToArray();
        EquationFamily = result.EquationFamily
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
        EquationDiscoveryStatusText = result.StatusText;
        EquationDiscoveryMetaText = result.MetaModelSummary;
        EquationDiscoveryProfileModeText =
            "Current reduced model: z(s, tau), where s is aligned centreline position extracted from the original x-y-z AFM surface. " +
            "Tau is a latent progression variable inferred from ordered stage labels, not real time.";
        EquationDiscoveryStageMappingText = "Pseudo-time anchors: " + string.Join("  |  ",
            result.StageMapping
                .OrderBy(entry => entry.Value)
                .Select(entry => $"{ToStageLabel(entry.Key)} = {entry.Value:F2}"));
        EquationDiscoveryTermGuideText =
            "Term guide: z is AFM height, s is aligned centreline position from the original x-y-z AFM surface, dz/ds is the local slope, " +
            "d2z/ds2 is curvature, d3z/ds3 and d4z/ds4 capture higher-order shape change, and tau is latent growth progression rather than real time.";
        EquationDiscoveryOverlayLegendText =
            "Stage colours: Early = green, Middle = amber, Late = red. Solid lines = observed AFM stage profiles z(s). " +
            "Dashed lines = reconstructed z(s, tau) profiles from the top-ranked candidate at the matching stage anchors.";
        EquationDiscoveryProgressionLegendText =
            "Reconstructed pseudo-time progression of z(s, tau): the colours move from green near Early (tau≈0), through amber near Middle, toward red near Late (tau≈1).";
        EquationDiscoveryXAxisLabel = string.IsNullOrWhiteSpace(result.SpatialCoordinateLabel) ? "Aligned centreline position s [nm]" : result.SpatialCoordinateLabel;
        EquationDiscoveryYAxisLabel = string.IsNullOrWhiteSpace(result.HeightLabel) ? "Height z [nm]" : result.HeightLabel;
        EquationTermExplanations = BuildGenericEquationTermExplanations();
        EquationOverlaySeries = BuildEquationOverlaySeries(result);
        EquationProgressionSeries = BuildEquationProgressionSeries(result);
    }

    private void ClearEquationDiscoveryResults(string? status = null)
    {
        _equationDiscoveryResult = null;
        _simulationEquationCandidates = Array.Empty<SimulationEquationCandidate>();
        EquationDiscoveryStageProfiles = Array.Empty<EquationDiscoveryStageProfile>();
        EquationFamily = Array.Empty<EquationCandidateResult>();
        EquationOverlaySeries = Array.Empty<PolylineSeries>();
        EquationProgressionSeries = Array.Empty<PolylineSeries>();
        EquationDiscoveryStatusText = status ?? "Use guided, stage-labelled profiles to discover a family of pseudo-time progression equations.";
        EquationDiscoveryMetaText = "Pseudo-time tau is an ordered latent progression variable derived from stage labels, not real clock time.";
        EquationDiscoveryStageMappingText = "Stage anchors: early = 0.00, middle = 0.50, late = 1.00";
        EquationDiscoveryProfileModeText = "Current reduced model: z(s, tau), where s is aligned centreline position extracted from the original x-y-z AFM surface.";
        EquationDiscoveryOverlayLegendText = "Stage colours: Early = green, Middle = amber, Late = red. Solid lines = observed AFM stage profiles z(s). Dashed lines = reconstructed z(s, tau) profiles from the top-ranked candidate at the matching stage anchors.";
        EquationDiscoveryProgressionLegendText = "Reconstructed pseudo-time progression of z(s, tau): the colours move from green near Early (tau≈0), through amber near Middle, toward red near Late (tau≈1).";
        EquationDiscoveryTermGuideText = "Term guide: z is AFM height, s is aligned centreline position from the original x-y-z AFM surface, dz/ds is the local slope, d2z/ds2 is curvature, d3z/ds3 and d4z/ds4 capture higher-order shape change, and tau is latent growth progression rather than real time.";
        EquationDiscoveryXAxisLabel = "Aligned centreline position s [nm]";
        EquationDiscoveryYAxisLabel = "Height z [nm]";
        EquationTermExplanations = BuildGenericEquationTermExplanations();
    }

    private static IReadOnlyList<PolylineSeries> BuildEquationOverlaySeries(EquationDiscoveryResult result)
    {
        var stageColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["early"] = "#68a65b",
            ["middle"] = "#d0a74d",
            ["late"] = "#cf6a4d"
        };

        var series = new List<PolylineSeries>();
        foreach (var curve in result.ObservedProfiles.OrderBy(curve => curve.Tau))
        {
            if (curve.Points.Count < 2) continue;
            var color = stageColors.TryGetValue(curve.Stage, out var stageColor) ? stageColor : "#ead9bf";
            series.Add(new PolylineSeries(curve.Points.Select(point => point.ToPlotPoint()).ToArray(), color, 2.6, 0.98));
        }

        foreach (var curve in result.ReconstructedProfiles.OrderBy(curve => curve.Tau))
        {
            if (curve.Points.Count < 2) continue;
            var color = stageColors.TryGetValue(curve.Stage, out var stageColor) ? stageColor : "#fff4d8";
            series.Add(new PolylineSeries(curve.Points.Select(point => point.ToPlotPoint()).ToArray(), color, 2.0, 0.72, Dashed: true));
        }

        return series;
    }

    private static IReadOnlyList<PolylineSeries> BuildEquationProgressionSeries(EquationDiscoveryResult result)
    {
        var palette = new[]
        {
            "#6da65f",
            "#92b76d",
            "#c2b16a",
            "#d5a55f",
            "#cf8658",
            "#c86f50",
            "#bf5d49"
        };

        return result.ProgressionProfiles
            .OrderBy(curve => curve.Tau)
            .Select((curve, index) => new PolylineSeries(
                curve.Points.Select(point => point.ToPlotPoint()).ToArray(),
                palette[Math.Min(index, palette.Length - 1)],
                1.9,
                0.98))
            .ToArray();
    }

    private void ApplySimulationAlignedEquationFamilyIfAvailable()
    {
        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || simulation.Frames.Count < 3) return;

        var candidates = BuildSimulationEquationCandidates(simulation);
        if (candidates.Count == 0) return;

        _simulationEquationCandidates = candidates;
        EquationFamily = candidates.Select(candidate => candidate.Display).ToArray();
        EquationOverlaySeries = BuildSimulationEquationOverlaySeries(simulation, candidates[0]);
        EquationProgressionSeries = BuildSimulationEquationProgressionSeries(simulation, candidates[0]);
        EquationDiscoveryStatusText = "Showing the simulation-aligned bimodal evolution equations derived from the same centered Gaussian trajectory used by the Growth Model tab.";
        EquationDiscoveryProfileModeText =
            "Current reduced model: z(s, tau) is represented as a centered bimodal Gaussian whose amplitudes, widths, and peak separation evolve over pseudo-time tau.";
        EquationDiscoveryMetaText =
            "The equations listed below are now fit directly to the same bimodal evolution shown in the simulation, so the displayed law matches the solid fitted curve in the Growth Model tab.";
        EquationDiscoveryOverlayLegendText =
            "Stage colours: Early = green, Middle = amber, Late = red. Solid lines = centered bimodal profiles extracted from the simulation references. Dashed lines = profiles reconstructed from the parameter-evolution equations at those same pseudo-time anchors.";
        EquationDiscoveryProgressionLegendText =
            "Coloured curves show the same centered bimodal Gaussian evolution law used by the Growth Model simulation, expressed here as parameter equations over pseudo-time tau.";
        EquationDiscoveryTermGuideText =
            "Bimodal model guide: z(s, tau) is explicitly written as z_L(s, tau) + z_R(s, tau), so the discovered family remains two-peaked across pseudo-time rather than collapsing to a single Gaussian. A_L and A_R are the left and right peak heights, sigma_L and sigma_R are the corresponding Gaussian widths, Delta is the peak-to-peak spacing, s_c is the fixed centred reference position, and tau is latent stage progression rather than real time.";
        EquationTermExplanations = BuildBimodalEquationTermExplanations();
    }

    private IReadOnlyList<SimulationEquationCandidate> BuildSimulationEquationCandidates(SurfaceSimulationResult simulation)
    {
        var frameRows = new List<(double Tau, double[] Parameters, IReadOnlyList<PlotPoint> Profile)>();
        for (var i = 0; i < simulation.Frames.Count; i++)
        {
            var parameters = _analysis.ExtractCenteredBimodalSimulationParameters(
                simulation.Frames[i],
                simulation.Width,
                simulation.Height,
                simulation.ScanSizeNmX);
            if (parameters.Length < 5) return Array.Empty<SimulationEquationCandidate>();

            var profile = _analysis.BuildCenteredBimodalSimulationProfile(
                simulation.Frames[i],
                simulation.Width,
                simulation.Height,
                simulation.ScanSizeNmX);
            if (profile.Count < 8) return Array.Empty<SimulationEquationCandidate>();

            frameRows.Add((simulation.FrameProgresses[i], parameters, profile));
        }

        if (frameRows.Count < 3) return Array.Empty<SimulationEquationCandidate>();

        var maxDegree = Math.Max(1, simulation.PolynomialDegree);
        var provisional = new List<(SimulationEquationCandidate Candidate, double Score)>();
        var taus = frameRows.Select(row => row.Tau).ToArray();

        for (var degree = 1; degree <= maxDegree; degree++)
        {
            var leftAmplitude = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[0]).ToArray(), degree);
            var leftSigma = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[1]).ToArray(), degree);
            var rightAmplitude = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[2]).ToArray(), degree);
            var rightSigma = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[3]).ToArray(), degree);
            var separation = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[4]).ToArray(), degree);
            if (leftAmplitude.Length == 0 || leftSigma.Length == 0 || rightAmplitude.Length == 0 || rightSigma.Length == 0 || separation.Length == 0)
            {
                continue;
            }

            double rmse = 0;
            double peakError = 0;
            double widthError = 0;
            double areaError = 0;
            double compromiseConsistency = 0;
            var stable = true;

            foreach (var row in frameRows)
            {
                var predictedParameters = ClampBimodalParameters(EvaluateSimulationEquationCandidate(
                    leftAmplitude,
                    leftSigma,
                    rightAmplitude,
                    rightSigma,
                    separation,
                    row.Tau));
                var predictedProfile = _analysis.BuildCenteredBimodalProfileFromParameters(predictedParameters, simulation.Width, simulation.ScanSizeNmX);
                if (predictedProfile.Count != row.Profile.Count || predictedProfile.Count == 0)
                {
                    stable = false;
                    continue;
                }

                rmse += ComputeProfileRmse(row.Profile, predictedProfile);
                peakError += Math.Abs(ComputeProfilePeakHeight(row.Profile) - ComputeProfilePeakHeight(predictedProfile));
                widthError += Math.Abs(ComputeProfileWidthNm(row.Profile) - ComputeProfileWidthNm(predictedProfile));
                areaError += Math.Abs(ComputeProfileArea(row.Profile) - ComputeProfileArea(predictedProfile));
                var dipDelta = Math.Abs(ComputeProfileDipDepthNm(row.Profile) - ComputeProfileDipDepthNm(predictedProfile));
                compromiseConsistency += Math.Max(0, 1.0 - dipDelta / Math.Max(1.0, ComputeProfilePeakHeight(row.Profile)));
            }

            var count = Math.Max(1, frameRows.Count);
            rmse /= count;
            peakError /= count;
            widthError /= count;
            areaError /= count;
            compromiseConsistency /= count;
            var stability = stable ? 1.0 : 0.45;
            var complexity = degree / 3.0;
            var metaPrior = degree == simulation.PolynomialDegree ? 1.0 : 0.78;
            var sensitivity = Math.Abs(widthError) / Math.Max(1.0, simulation.ScanSizeNmX);
            var confidence = Math.Clamp(
                0.35 * stability +
                0.22 * (1.0 / (1.0 + rmse)) +
                0.14 * (1.0 / (1.0 + peakError)) +
                0.12 * (1.0 / (1.0 + widthError)) +
                0.07 * (1.0 / (1.0 + areaError / Math.Max(1.0, simulation.ScanSizeNmX))) +
                0.10 * metaPrior,
                0.0,
                1.0);

            var coefficientStats = BuildSimulationCoefficientStatistics(
                leftAmplitude,
                leftSigma,
                rightAmplitude,
                rightSigma,
                separation);

            var display = new EquationCandidateResult
            {
                Rank = 0,
                Equation = BuildSimulationEquationText(leftAmplitude, leftSigma, rightAmplitude, rightSigma, separation),
                ActiveTerms = new[] { "z_L(s,τ)", "z_R(s,τ)", "A_L(τ)", "σ_L(τ)", "A_R(τ)", "σ_R(τ)", "s_L(τ)", "s_R(τ)", "Δ(τ)", "s_c" },
                Coefficients = coefficientStats.ToDictionary(entry => entry.Key, entry => entry.Value.Mean, StringComparer.OrdinalIgnoreCase),
                CoefficientStatistics = coefficientStats,
                Rmse = rmse,
                PeakHeightError = peakError,
                WidthError = widthError,
                AreaError = areaError,
                CompromiseConsistency = compromiseConsistency,
                StabilityScore = stability,
                ComplexityPenalty = complexity,
                Confidence = confidence,
                PseudotimeSensitivity = sensitivity,
                BootstrapSupport = 1.0,
                MetaPriorScore = metaPrior,
                Notes =
                    $"Derived from the same centered bimodal Gaussian trajectory used in Growth Model. " +
                    $"At every pseudo-time τ the profile is reconstructed as the sum of two Gaussian peaks, while polynomial degree {degree} controls how A_L, σ_L, A_R, σ_R, and Δ evolve."
            };

            provisional.Add((new SimulationEquationCandidate
            {
                Display = display,
                Degree = degree,
                LeftAmplitudeCoefficients = leftAmplitude,
                LeftSigmaCoefficients = leftSigma,
                RightAmplitudeCoefficients = rightAmplitude,
                RightSigmaCoefficients = rightSigma,
                SeparationCoefficients = separation
            }, rmse + 0.15 * peakError + 0.08 * widthError + 0.04 * areaError - 0.10 * stability - 0.04 * metaPrior));
        }

        return provisional
            .OrderBy(entry => entry.Score)
            .ThenByDescending(entry => entry.Candidate.Display.Confidence)
            .Select((entry, index) => new SimulationEquationCandidate
            {
                Display = new EquationCandidateResult
                {
                    Rank = index + 1,
                    Equation = entry.Candidate.Display.Equation,
                    ActiveTerms = entry.Candidate.Display.ActiveTerms,
                    Coefficients = entry.Candidate.Display.Coefficients,
                    CoefficientStatistics = entry.Candidate.Display.CoefficientStatistics,
                    Rmse = entry.Candidate.Display.Rmse,
                    PeakHeightError = entry.Candidate.Display.PeakHeightError,
                    WidthError = entry.Candidate.Display.WidthError,
                    AreaError = entry.Candidate.Display.AreaError,
                    CompromiseConsistency = entry.Candidate.Display.CompromiseConsistency,
                    StabilityScore = entry.Candidate.Display.StabilityScore,
                    ComplexityPenalty = entry.Candidate.Display.ComplexityPenalty,
                    Confidence = entry.Candidate.Display.Confidence,
                    PseudotimeSensitivity = entry.Candidate.Display.PseudotimeSensitivity,
                    BootstrapSupport = entry.Candidate.Display.BootstrapSupport,
                    MetaPriorScore = entry.Candidate.Display.MetaPriorScore,
                    Notes = entry.Candidate.Display.Notes
                },
                Degree = entry.Candidate.Degree,
                LeftAmplitudeCoefficients = entry.Candidate.LeftAmplitudeCoefficients,
                LeftSigmaCoefficients = entry.Candidate.LeftSigmaCoefficients,
                RightAmplitudeCoefficients = entry.Candidate.RightAmplitudeCoefficients,
                RightSigmaCoefficients = entry.Candidate.RightSigmaCoefficients,
                SeparationCoefficients = entry.Candidate.SeparationCoefficients
            })
            .ToArray();
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationEquationOverlaySeries(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
        var stageColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["early"] = "#68a65b",
            ["middle"] = "#d0a74d",
            ["late"] = "#cf6a4d"
        };

        var series = new List<PolylineSeries>();
        for (var i = 0; i < simulation.References.Count && i < simulation.ReferenceSurfaces.Count; i++)
        {
            var reference = simulation.References[i];
            var actual = _analysis.BuildCenteredBimodalSimulationProfile(
                simulation.ReferenceSurfaces[i],
                simulation.Width,
                simulation.Height,
                simulation.ScanSizeNmX);
            if (actual.Count < 2) continue;

            var predictedParameters = EvaluateSimulationEquationCandidate(candidate, reference.Position01);
            var predicted = _analysis.BuildCenteredBimodalProfileFromParameters(predictedParameters, simulation.Width, simulation.ScanSizeNmX);
            var color = stageColors.TryGetValue(reference.Stage, out var stageColor) ? stageColor : "#ead9bf";
            series.Add(new PolylineSeries(actual.ToArray(), color, 2.6, 0.98));
            if (predicted.Count > 1)
            {
                series.Add(new PolylineSeries(predicted.ToArray(), color, 2.0, 0.74, Dashed: true));
            }
        }

        return series;
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationEquationProgressionSeries(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
        var palette = new[]
        {
            "#6da65f",
            "#92b76d",
            "#c2b16a",
            "#d5a55f",
            "#cf8658",
            "#c86f50",
            "#bf5d49"
        };

        return Enumerable.Range(0, 7)
            .Select(index => index / 6.0)
            .Select((tau, index) =>
            {
                var parameters = EvaluateSimulationEquationCandidate(candidate, tau);
                var profile = _analysis.BuildCenteredBimodalProfileFromParameters(parameters, simulation.Width, simulation.ScanSizeNmX);
                return new PolylineSeries(profile.ToArray(), palette[Math.Min(index, palette.Length - 1)], 1.9, 0.98);
            })
            .Where(series => series.Points.Count > 1)
            .ToArray();
    }

    private double[] EvaluateSimulationEquationCandidate(SimulationEquationCandidate candidate, double tau)
    {
        return EvaluateSimulationEquationCandidate(
            candidate.LeftAmplitudeCoefficients,
            candidate.LeftSigmaCoefficients,
            candidate.RightAmplitudeCoefficients,
            candidate.RightSigmaCoefficients,
            candidate.SeparationCoefficients,
            tau);
    }

    private double[] EvaluateSimulationEquationCandidate(
        IReadOnlyList<double> leftAmplitude,
        IReadOnlyList<double> leftSigma,
        IReadOnlyList<double> rightAmplitude,
        IReadOnlyList<double> rightSigma,
        IReadOnlyList<double> separation,
        double tau)
    {
        return ClampBimodalParameters(
        [
            _analysis.EvaluatePolynomialCurve(leftAmplitude, tau),
            _analysis.EvaluatePolynomialCurve(leftSigma, tau),
            _analysis.EvaluatePolynomialCurve(rightAmplitude, tau),
            _analysis.EvaluatePolynomialCurve(rightSigma, tau),
            _analysis.EvaluatePolynomialCurve(separation, tau)
        ]);
    }

    private static double[] ClampBimodalParameters(IReadOnlyList<double> parameters)
    {
        if (parameters.Count < 5) return Array.Empty<double>();
        return
        [
            Math.Max(0, parameters[0]),
            Math.Max(1e-3, Math.Abs(parameters[1])),
            Math.Max(0, parameters[2]),
            Math.Max(1e-3, Math.Abs(parameters[3])),
            Math.Max(0, Math.Abs(parameters[4]))
        ];
    }

    private static string BuildSimulationEquationText(
        IReadOnlyList<double> leftAmplitude,
        IReadOnlyList<double> leftSigma,
        IReadOnlyList<double> rightAmplitude,
        IReadOnlyList<double> rightSigma,
        IReadOnlyList<double> separation)
    {
        return string.Join(Environment.NewLine,
            "z(s, τ) = z_L(s, τ) + z_R(s, τ)",
            "z_L(s, τ) = A_L(τ) · exp(-((s - s_L(τ))²) / (2σ_L(τ)²))",
            "z_R(s, τ) = A_R(τ) · exp(-((s - s_R(τ))²) / (2σ_R(τ)²))",
            "s_L(τ) = s_c - Δ(τ)/2",
            "s_R(τ) = s_c + Δ(τ)/2",
            $"A_L(τ) = {BuildPolynomialText(leftAmplitude)}",
            $"σ_L(τ) = {BuildPolynomialText(leftSigma)}",
            $"A_R(τ) = {BuildPolynomialText(rightAmplitude)}",
            $"σ_R(τ) = {BuildPolynomialText(rightSigma)}",
            $"Δ(τ) = {BuildPolynomialText(separation)}");
    }

    private static string BuildPolynomialText(IReadOnlyList<double> coefficients)
    {
        if (coefficients.Count == 0) return "0";
        var parts = new List<string>();
        for (var i = 0; i < coefficients.Count; i++)
        {
            var coefficient = coefficients[i];
            if (Math.Abs(coefficient) < 1e-9) continue;
            var magnitude = Math.Abs(coefficient).ToString("F4", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            var basis = i switch
            {
                0 => string.Empty,
                1 => "τ",
                _ => $"τ{ToSuperscript(i)}"
            };
            var fragment = string.IsNullOrEmpty(basis) ? magnitude : $"{magnitude}·{basis}";
            parts.Add((coefficient < 0 ? "- " : "+ ") + fragment);
        }

        if (parts.Count == 0) return "0";
        return string.Join(" ", parts).TrimStart('+', ' ');
    }

    private static string ToSuperscript(int value)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        var output = new StringBuilder(digits.Length);
        foreach (var digit in digits)
        {
            output.Append(digit switch
            {
                '0' => '⁰',
                '1' => '¹',
                '2' => '²',
                '3' => '³',
                '4' => '⁴',
                '5' => '⁵',
                '6' => '⁶',
                '7' => '⁷',
                '8' => '⁸',
                '9' => '⁹',
                '-' => '⁻',
                _ => digit
            });
        }

        return output.ToString();
    }

    private static IReadOnlyList<EquationTermExplanation> BuildGenericEquationTermExplanations() =>
    [
        new EquationTermExplanation
        {
            Symbol = "z(s, τ)",
            Meaning = "Height profile over pseudo-time",
            Detail = "The discovered law evolves AFM height z along the aligned centreline coordinate s as latent growth progression τ increases."
        },
        new EquationTermExplanation
        {
            Symbol = "s",
            Meaning = "Aligned centreline position",
            Detail = "Distance along the guided piecrust centreline extracted from the original x-y-z AFM surface."
        },
        new EquationTermExplanation
        {
            Symbol = "τ",
            Meaning = "Pseudo-time / progression variable",
            Detail = "Ordered stage progression anchor, not real clock time."
        },
        new EquationTermExplanation
        {
            Symbol = "dz/ds",
            Meaning = "Local slope",
            Detail = "How fast the profile height changes along the aligned centreline."
        },
        new EquationTermExplanation
        {
            Symbol = "d²z/ds²",
            Meaning = "Curvature",
            Detail = "How sharply the profile bends; positive and negative values indicate different local shape changes."
        },
        new EquationTermExplanation
        {
            Symbol = "d³z/ds³, d⁴z/ds⁴",
            Meaning = "Higher-order shape terms",
            Detail = "Used only in the reduced discovery mode to capture sharper rim formation and smoothing behaviour."
        }
    ];

    private static IReadOnlyList<EquationTermExplanation> BuildBimodalEquationTermExplanations() =>
    [
        new EquationTermExplanation
        {
            Symbol = "z(s, τ)",
            Meaning = "Total reconstructed piecrust profile",
            Detail = "This is explicitly the sum of two peaks, z_L + z_R, so the discovered evolution remains bimodal."
        },
        new EquationTermExplanation
        {
            Symbol = "z_L(s, τ), z_R(s, τ)",
            Meaning = "Left and right Gaussian rim profiles",
            Detail = "Each side of the piecrust is modelled as its own Gaussian contribution before the two are added together."
        },
        new EquationTermExplanation
        {
            Symbol = "A_L(τ), A_R(τ)",
            Meaning = "Left and right peak heights",
            Detail = "These polynomial laws control how tall each piecrust rim becomes as progression increases."
        },
        new EquationTermExplanation
        {
            Symbol = "σ_L(τ), σ_R(τ)",
            Meaning = "Left and right peak widths",
            Detail = "These determine how broad or narrow each Gaussian rim is at a given pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "s_L(τ), s_R(τ)",
            Meaning = "Left and right peak centres",
            Detail = "These are the two peak positions. They are separated symmetrically around the centred reference position s_c."
        },
        new EquationTermExplanation
        {
            Symbol = "Δ(τ)",
            Meaning = "Peak-to-peak separation",
            Detail = "Controls how far apart the two bimodal rims are at each pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "s_c",
            Meaning = "Centred reference position",
            Detail = "The fixed aligned centre of the guided profile. The left and right peaks move around this anchor."
        },
        new EquationTermExplanation
        {
            Symbol = "τ",
            Meaning = "Pseudo-time / stage progression",
            Detail = "A latent progression coordinate inferred from early, middle, and late ordering rather than true time-lapse kinetics."
        }
    ];

    private static Dictionary<string, EquationCoefficientStatistics> BuildSimulationCoefficientStatistics(
        IReadOnlyList<double> leftAmplitude,
        IReadOnlyList<double> leftSigma,
        IReadOnlyList<double> rightAmplitude,
        IReadOnlyList<double> rightSigma,
        IReadOnlyList<double> separation)
    {
        var output = new Dictionary<string, EquationCoefficientStatistics>(StringComparer.OrdinalIgnoreCase);
        AddPolynomialStats(output, "A_L", leftAmplitude);
        AddPolynomialStats(output, "sigma_L", leftSigma);
        AddPolynomialStats(output, "A_R", rightAmplitude);
        AddPolynomialStats(output, "sigma_R", rightSigma);
        AddPolynomialStats(output, "Delta", separation);
        return output;
    }

    private static void AddPolynomialStats(IDictionary<string, EquationCoefficientStatistics> output, string prefix, IReadOnlyList<double> coefficients)
    {
        for (var i = 0; i < coefficients.Count; i++)
        {
            var value = coefficients[i];
            output[$"{prefix}:c{i}"] = new EquationCoefficientStatistics
            {
                Mean = value,
                StandardDeviation = 0,
                Lower95 = value,
                Upper95 = value
            };
        }
    }

    private static double ComputeProfileRmse(IReadOnlyList<PlotPoint> a, IReadOnlyList<PlotPoint> b)
    {
        var count = Math.Min(a.Count, b.Count);
        if (count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var delta = a[i].Y - b[i].Y;
            sum += delta * delta;
        }

        return Math.Sqrt(sum / count);
    }

    private static double ComputeProfilePeakHeight(IReadOnlyList<PlotPoint> profile) => profile.Count == 0 ? 0 : profile.Max(point => point.Y);

    private static double ComputeProfileArea(IReadOnlyList<PlotPoint> profile)
    {
        if (profile.Count < 2) return 0;
        double area = 0;
        for (var i = 1; i < profile.Count; i++)
        {
            area += (profile[i].X - profile[i - 1].X) * (profile[i].Y + profile[i - 1].Y) / 2.0;
        }

        return area;
    }

    private static double ComputeProfileWidthNm(IReadOnlyList<PlotPoint> profile)
    {
        if (profile.Count < 3) return 0;
        var peak = ComputeProfilePeakHeight(profile);
        if (!(peak > 1e-9)) return 0;
        var half = peak * 0.5;
        var maxIndex = profile
            .Select((point, index) => (point.Y, index))
            .OrderByDescending(item => item.Y)
            .First().index;
        var left = maxIndex;
        var right = maxIndex;
        while (left > 0 && profile[left].Y >= half) left--;
        while (right < profile.Count - 1 && profile[right].Y >= half) right++;
        return Math.Max(0, profile[right].X - profile[left].X);
    }

    private static double ComputeProfileDipDepthNm(IReadOnlyList<PlotPoint> profile)
    {
        if (profile.Count < 5) return 0;
        var mid = profile.Count / 2;
        var leftIndex = profile.Take(mid).Select((point, index) => (point.Y, index)).OrderByDescending(item => item.Y).First().index;
        var rightIndex = mid + profile.Skip(mid).Select((point, index) => (point.Y, index)).OrderByDescending(item => item.Y).First().index;
        if (rightIndex <= leftIndex + 1) return 0;
        var valley = profile.Skip(leftIndex).Take(rightIndex - leftIndex + 1).Min(point => point.Y);
        var meanPeak = (profile[leftIndex].Y + profile[rightIndex].Y) / 2.0;
        return Math.Max(0, meanPeak - valley);
    }

    private void RefreshEvolutionSeries()
    {
        var buckets = _analysis.BuildEvolutionBuckets(Files);
        var lines = new List<PolylineSeries>();
        var stageColors = new Dictionary<string, string>
        {
            ["early"] = "#4a8a4a",
            ["middle"] = "#8a8a4a",
            ["late"] = "#8a4a4a"
        };
        foreach (var stage in new[] { "early", "middle", "late" })
        {
            var bucket = buckets[stage];
            foreach (var record in bucket.Records)
            {
                var points = ToRelativePercentPoints(record.Profile);
                lines.Add(new PolylineSeries(points, stageColors[stage], 1.0, 0.28));
            }
            var centeredMean = _analysis.BuildCenteredStageOverlayProfile(bucket);
            if (centeredMean is { Length: > 0 })
            {
                var meanPoints = ToRelativePercentPoints(centeredMean);
                lines.Add(new PolylineSeries(meanPoints, stageColors[stage], 2.2, 1.0));
            }
        }

        var predicted = _analysis.PredictEvolutionProfile(buckets, EvolutionProgress);
        if (predicted.Count > 0) lines.Add(new PolylineSeries(predicted, "#fff5d8", 2.0, 1.0, true));
        EvolutionSeries = lines;
    }

    public void ToggleSimulationPlayback()
    {
        if (SimulationStartFile is null || SimulationEndFile is null)
        {
            StatusText = "Choose start and end reference files before running the simulation.";
            return;
        }

        if (ReferenceEquals(SimulationStartFile, SimulationEndFile))
        {
            StatusText = "Choose two different reference files for the simulation.";
            return;
        }

        if (IsSimulationPlaying)
        {
            StopSimulationPlayback();
            return;
        }

        if (SimulationProgress >= 1) SimulationProgress = 0;
        IsSimulationPlaying = true;
        _simulationTimer.Start();
    }

    public void RunPolynomialEvolution()
    {
        StopSimulationPlayback();
        if (SimulationStartFile is null || SimulationEndFile is null)
        {
            StatusText = "Choose start and end reference files before running the polynomial evolution.";
            return;
        }

        if (ReferenceEquals(SimulationStartFile, SimulationEndFile))
        {
            StatusText = "Choose two different reference files for the polynomial evolution.";
            return;
        }

        InvalidateSimulationCache();
        SimulationProgress = 0;
        RefreshSimulationSeries();
        if (_surfaceSimulationCache is null)
        {
            StatusText = "The selected references did not generate a usable polynomial evolution.";
            return;
        }

        StatusText = "Polynomial evolution prepared. Use Play / Pause Simulation to animate the fitted growth path.";
        PersistSessionIfPossible();
    }

    public void ResetSimulationPlayback()
    {
        StopSimulationPlayback();
        SimulationProgress = 0;
        RefreshSimulationSeries();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    public void SyncSelectedFileDisplayMetrics()
    {
        CurrentCorridorHalfWidthPixels = SelectedFile is null
            ? 8
            : Math.Max(2, SelectedFile.GuideCorridorWidthNm / Math.Max(1e-9, SelectedFile.NmPerPixel) / 2.0);
        CurrentCorridorWidthOverlayText = SelectedFile is null
            ? "Corridor: -"
            : $"Corridor: {SelectedFile.GuideCorridorWidthNm:F1} {SelectedFile.Unit}";
    }

    private void SyncSelectedDisplayControlsFromSelectedFile()
    {
        _syncingSelectedDisplayControls = true;
        try
        {
            if (SelectedFile is null)
            {
                SelectedDisplayRangeMode = "auto";
                SelectedDisplayBoundsMin = 0;
                SelectedDisplayBoundsMax = 1;
                SelectedDisplayRangeMin = 0;
                SelectedDisplayRangeMax = 1;
                SelectedDisplaySliderStep = 0.1;
                SelectedDisplayHistogram = Array.Empty<double>();
                SelectedDisplayWindowStartPercent = 0;
                SelectedDisplayWindowEndPercent = 100;
                return;
            }

            var bounds = (SelectedFile.DisplayRangeFullMin, SelectedFile.DisplayRangeFullMax);
            var fixedRange = _heightMapDisplay.ClampRange(bounds, SelectedFile.FixedDisplayMin, SelectedFile.FixedDisplayMax);
            SelectedDisplayRangeMode = SelectedFile.DisplayRangeMode;
            SelectedDisplayBoundsMin = bounds.Item1;
            SelectedDisplayBoundsMax = bounds.Item2;
            SelectedDisplayRangeMin = fixedRange.Min;
            SelectedDisplayRangeMax = fixedRange.Max;
            SelectedDisplaySliderStep = _heightMapDisplay.GetSliderStep(bounds.Item1, bounds.Item2);
            SelectedDisplayHistogram = _heightMapDisplay.BuildHistogram(
                SelectedFile.DisplayHeightData.Length > 0 ? SelectedFile.DisplayHeightData : SelectedFile.HeightData,
                bounds.Item1,
                bounds.Item2);
            SelectedDisplayWindowStartPercent = _heightMapDisplay.RangePercent(SelectedFile.DisplayMin, bounds.Item1, bounds.Item2);
            SelectedDisplayWindowEndPercent = _heightMapDisplay.RangePercent(SelectedFile.DisplayMax, bounds.Item1, bounds.Item2);
        }
        finally
        {
            _syncingSelectedDisplayControls = false;
        }
    }

    private void ApplySelectedDisplaySettingsToSelectedFile()
    {
        if (_syncingSelectedDisplayControls || SelectedFile is null) return;

        SelectedFile.DisplayRangeMode = SelectedDisplayRangeMode;
        var fixedRange = _heightMapDisplay.ClampRange(
            (SelectedFile.DisplayRangeFullMin, SelectedFile.DisplayRangeFullMax),
            SelectedDisplayRangeMin,
            SelectedDisplayRangeMax);
        SelectedFile.FixedDisplayMin = fixedRange.Min;
        SelectedFile.FixedDisplayMax = fixedRange.Max;

        ApplyDisplaySettingsToFile(SelectedFile);
        SyncSelectedDisplayControlsFromSelectedFile();
        RefreshSelectedSummaryText();
        PersistSessionIfPossible();
    }

    private void ApplyDisplaySettingsToFile(PiecrustFileState file)
    {
        var bounds = _heightMapDisplay.ClampRange(
            (file.DisplayRangeFullMin, file.DisplayRangeFullMax),
            file.DisplayRangeFullMin,
            file.DisplayRangeFullMax);
        file.DisplayRangeFullMin = bounds.Min;
        file.DisplayRangeFullMax = bounds.Max;

        var autoRange = _heightMapDisplay.ClampRange(bounds, file.DisplayRangeAutoMin, file.DisplayRangeAutoMax);
        file.DisplayRangeAutoMin = autoRange.Min;
        file.DisplayRangeAutoMax = autoRange.Max;

        var fixedRange = _heightMapDisplay.ClampRange(bounds, file.FixedDisplayMin, file.FixedDisplayMax);
        file.FixedDisplayMin = fixedRange.Min;
        file.FixedDisplayMax = fixedRange.Max;

        var activeRange = file.DisplayRangeMode switch
        {
            "full" => bounds,
            "fixed" => fixedRange,
            _ => autoRange
        };

        file.DisplayMin = activeRange.Min;
        file.DisplayMax = activeRange.Max;

        var displayData = file.DisplayHeightData.Length > 0 ? file.DisplayHeightData : file.HeightData;
        var rendered = _previewBitmapService.Render(displayData, file.PixelWidth, file.PixelHeight, activeRange.Min, activeRange.Max, scientificPreview: false);
        file.PreviewBitmap = rendered.Bitmap;
    }

    public void RefreshAfterSelectionEdit()
    {
        SyncSelectedFileDisplayMetrics();
        RefreshDerivedState();
        PersistSessionIfPossible();
    }

    public void SetAutoDisplayAsFixed()
    {
        if (SelectedFile is null) return;
        _syncingSelectedDisplayControls = true;
        try
        {
            SelectedDisplayRangeMode = "fixed";
            SelectedDisplayRangeMin = SelectedFile.DisplayRangeAutoMin;
            SelectedDisplayRangeMax = SelectedFile.DisplayRangeAutoMax;
        }
        finally
        {
            _syncingSelectedDisplayControls = false;
        }

        ApplySelectedDisplaySettingsToSelectedFile();
        StatusText = $"Auto colour window copied into the fixed display range for {SelectedFile.Name}.";
    }

    private void RefreshSelectedSummaryText()
    {
        if (SelectedFile is null)
        {
            SelectedFileNameText = "No file selected";
            SelectedChannelDisplayText = "Channel: -";
            SelectedDisplayMinText = "Display Min: -";
            SelectedDisplayMaxText = "Display Max: -";
            SelectedDisplayReferenceText = "Display Reference: -";
            SelectedEstimatedNoiseText = "Estimated Noise Sigma: -";
            SelectedMeanHeightText = "Mean Height: run guided extraction";
            SelectedMeanWidthText = "Mean Width: run guided extraction";
            SelectedHeightWidthRatioText = "Height/Width Ratio: -";
            SelectedContinuityText = "Continuity: -";
            SelectedPeakSeparationText = "Peak Separation: -";
            CurrentAdditionRateText = "Addition Rate: -";
            CurrentRemovalRateText = "Removal Rate: -";
            CurrentCompromiseText = "Compromise: -";
            CurrentProfileXAxisLabel = "x [nm]";
            CurrentProfileYAxisLabel = "y [nm]";
            EvolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
            EvolutionYAxisLabel = "Baseline-shifted height [nm]";
            SimulationXAxisLabel = "Aligned x [nm]";
            SimulationYAxisLabel = "Simulated height [nm]";
            SimulationReferenceSummaryText = "Ordered references: -";
            SimulationSurfaceMetaText = "Surface frame: -";
            SimulationSurfaceXAxisLabel = "Aligned x [nm]";
            SimulationSurfaceYAxisLabel = "Aligned y [nm]";
            SelectedStageHintText = "Stage is auto-classified on load, but you can change it manually.";
            return;
        }

        SelectedFileNameText = SelectedFile.Name;
        SelectedChannelDisplayText = $"Channel: {SelectedFile.ChannelDisplay}";
        SelectedDisplayMinText = $"Display Min: {SelectedFile.DisplayMin:F2}";
        SelectedDisplayMaxText = $"Display Max: {SelectedFile.DisplayMax:F2}";
        SelectedDisplayReferenceText = $"Display Reference: {SelectedFile.DisplayReferenceNm:F2} {SelectedFile.Unit}";
        SelectedEstimatedNoiseText = $"Estimated Noise Sigma: {SelectedFile.EstimatedNoiseSigma:F3} {SelectedFile.Unit}";
        CurrentProfileXAxisLabel = $"x [{SelectedFile.Unit}]";
        CurrentProfileYAxisLabel = $"y [{SelectedFile.Unit}]";
        EvolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
        EvolutionYAxisLabel = $"Baseline-shifted height [{SelectedFile.Unit}]";
        SimulationXAxisLabel = $"Corridor offset [{SelectedFile.Unit}]";
        SimulationYAxisLabel = $"Simulated height [{SelectedFile.Unit}]";
        SimulationSurfaceXAxisLabel = $"Corridor offset [{SelectedFile.Unit}]";
        SimulationSurfaceYAxisLabel = $"Guide distance [{SelectedFile.Unit}]";
        SelectedStageHintText = $"Stage is currently '{SelectedFile.Stage}'. You can override the auto-assigned stage if this file belongs in a different phase.";

        if (SelectedFile.GuidedSummary is { } summary)
        {
            SelectedMeanHeightText = $"Mean Height: {summary.MeanHeightNm:F2} {SelectedFile.Unit}";
            SelectedMeanWidthText = $"Mean Width: {summary.MeanWidthNm:F2} {SelectedFile.Unit}";
            SelectedHeightWidthRatioText = $"Height/Width Ratio: {summary.HeightToWidthRatio:F4}";
            SelectedContinuityText = $"Continuity: {summary.Continuity:F3}";
            SelectedPeakSeparationText = $"Peak Separation: {summary.PeakSeparationNm:F2} {SelectedFile.Unit}";
        }
        else
        {
            SelectedMeanHeightText = "Mean Height: run guided extraction";
            SelectedMeanWidthText = "Mean Width: run guided extraction";
            SelectedHeightWidthRatioText = "Height/Width Ratio: run guided extraction";
            SelectedContinuityText = "Continuity: -";
            SelectedPeakSeparationText = "Peak Separation: -";
        }

        if (CurrentGrowthRow is { } growth)
        {
            CurrentAdditionRateText = $"Addition Rate: {growth.AdditionRateNm:F2} {SelectedFile.Unit}";
            CurrentRemovalRateText = $"Removal Rate: {growth.RemovalRateNm:F2} {SelectedFile.Unit}";
            CurrentCompromiseText = growth.ControlReferenceCount > 0
                ? $"Compromise vs control: {growth.CompromiseRatio:F3}"
                : $"Raw compromise: {growth.CompromiseRatio:F3}";
        }
        else
        {
            CurrentAdditionRateText = "Addition Rate: -";
            CurrentRemovalRateText = "Removal Rate: -";
            CurrentCompromiseText = "Compromise: -";
        }
    }

    partial void OnSelectedDisplayRangeModeChanged(string value) => ApplySelectedDisplaySettingsToSelectedFile();

    partial void OnSelectedDisplayRangeMinChanged(double value) => ApplySelectedDisplaySettingsToSelectedFile();

    partial void OnSelectedDisplayRangeMaxChanged(double value) => ApplySelectedDisplaySettingsToSelectedFile();

    private void RefreshCompromiseMethodText()
    {
        var controlRows = GrowthRows
            .Where(row => string.Equals(row.ConditionType, "control", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (controlRows.Length == 0)
        {
            CompromiseMethodText = "Compromise score currently uses the raw morphology balance only. Mark untreated files as control so the app can compare treated evolution against the stage-matched control reference profiles.";
            return;
        }

        var controlMean = controlRows.Average(row => row.CompromiseRatio);
        var controlSpread = controlRows.Length > 1
            ? Math.Sqrt(controlRows.Select(row => Math.Pow(row.CompromiseRatio - controlMean, 2)).Average())
            : 0;
        CompromiseMethodText =
            $"Compromise score is now control-normalized by stage: it combines raw morphology balance, height loss, removal gain, and evolution-profile deviation from the untreated control reference set. " +
            $"Untreated controls currently average {controlMean:F3}" +
            (controlRows.Length > 1 ? $" +/- {controlSpread:F3}" : string.Empty) +
            ", so treated samples are interpreted relative to that control baseline.";
    }

    private void RefreshSupervisedModel()
    {
        _supervisedGrowthModel = _supervisedGrowthLearning.RefreshModel(Files);
        SupervisedModelStatusText = _supervisedGrowthLearning.DescribeModel(_supervisedGrowthModel);
    }

    private void OnFileStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PiecrustFileState file) return;
        if (e.PropertyName is not (nameof(PiecrustFileState.Stage)
            or nameof(PiecrustFileState.ConditionType)
            or nameof(PiecrustFileState.AntibioticDoseUgPerMl)
            or nameof(PiecrustFileState.SequenceOrder)
            or nameof(PiecrustFileState.GuideCorridorWidthNm)
            or nameof(PiecrustFileState.GuidedSummary)))
        {
            return;
        }

        if (e.PropertyName is nameof(PiecrustFileState.SequenceOrder) or nameof(PiecrustFileState.Stage))
        {
            InvalidateSimulationCache();
        }

        ClearEquationDiscoveryResults();
        RefreshDerivedState();
    }

    partial void OnSimulationProgressChanged(double value)
    {
        RefreshSimulationSeries();
        if (!IsSimulationPlaying) PersistSessionIfPossible();
    }

    partial void OnSelectedTabIndexChanged(int value) => PersistSessionIfPossible();

    partial void OnSimulationStartFileChanged(PiecrustFileState? value)
    {
        StopSimulationPlayback();
        if (value is not null && ReferenceEquals(value, SimulationEndFile) && Files.Count > 1)
        {
            SimulationEndFile = Files.FirstOrDefault(f => !ReferenceEquals(f, value));
        }
        InvalidateSimulationCache();
        SimulationProgress = 0;
        RefreshSimulationSeries();
        PersistSessionIfPossible();
    }

    partial void OnSimulationEndFileChanged(PiecrustFileState? value)
    {
        StopSimulationPlayback();
        if (value is not null && ReferenceEquals(value, SimulationStartFile) && Files.Count > 1)
        {
            SimulationStartFile = Files.FirstOrDefault(f => !ReferenceEquals(f, value));
        }
        InvalidateSimulationCache();
        SimulationProgress = 0;
        RefreshSimulationSeries();
        PersistSessionIfPossible();
    }

    private void RefreshSimulationSeries()
    {
        if (SimulationStartFile is null || SimulationEndFile is null)
        {
            SimulationSurfaceBitmap = null;
            SimulationSeries = Array.Empty<PolylineSeries>();
            SimulationPlotFixedYMin = double.NaN;
            SimulationPlotFixedYMax = double.NaN;
            SimulationStatusText = "Select start and end reference files to run the full 2D growth simulation.";
            SimulationReferenceSummaryText = "Ordered references: -";
            SimulationSurfaceMetaText = "Surface frame: -";
            SupervisedModelStatusText = _supervisedGrowthLearning.DescribeModel(_supervisedGrowthModel);
            return;
        }

        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || simulation.Frames.Count == 0)
        {
            SimulationSurfaceBitmap = null;
            SimulationSeries = Array.Empty<PolylineSeries>();
            SimulationPlotFixedYMin = double.NaN;
            SimulationPlotFixedYMax = double.NaN;
            SimulationStatusText = "The selected references do not currently yield a usable 2D simulation.";
            SimulationReferenceSummaryText = "Ordered references: -";
            SimulationSurfaceMetaText = "Surface frame: -";
            SupervisedModelStatusText = _supervisedGrowthLearning.DescribeModel(_supervisedGrowthModel);
            return;
        }

        var currentFrame = _analysis.BuildInterpolatedSimulationFrame(simulation, SimulationProgress);
        SimulationSurfaceBitmap = null;
        SimulationXAxisLabel = simulation.UsesGuidedAlignment ? $"Corridor offset [{simulation.Unit}]" : $"Aligned x [{simulation.Unit}]";
        SimulationSurfaceXAxisLabel = simulation.UsesGuidedAlignment ? $"Corridor offset [{simulation.Unit}]" : $"Aligned x [{simulation.Unit}]";
        SimulationSurfaceYAxisLabel = simulation.UsesGuidedAlignment ? $"Guide distance [{simulation.Unit}]" : $"Aligned y [{simulation.Unit}]";
        SimulationSeries = BuildSimulationPlotSeries(simulation, currentFrame);
        SimulationPlotFixedYMin = 0;
        SimulationPlotFixedYMax = GetSimulationPlotYMax(simulation);
        SimulationReferenceSummaryText = BuildSimulationReferenceSummary(simulation);
        var alignmentText = simulation.UsesGuidedAlignment
            ? "The simulation now uses only the guided corridor region, widened to corridor + 20%, so the evolving profile keeps a little extra context around the extracted piecrust."
            : "Guided alignment was unavailable for one or more references, so full-image surfaces were used.";
        SimulationSurfaceMetaText = $"Simulation span {(int)Math.Round(SimulationProgress * (simulation.Frames.Count - 1)) + 1}/{simulation.Frames.Count}  |  cross-section width: 0-{simulation.ScanSizeNmX:F1} {simulation.Unit}  |  guide distance: 0-{simulation.ScanSizeNmY:F1} {simulation.Unit}  |  {alignmentText}";
        SimulationStatusText = simulation.UsesSupervisedLearning
            ? $"Polynomial gap-filling fit (degree {simulation.PolynomialDegree}) across {simulation.References.Count} ordered reference stage(s), guided by a supervised bimodal growth learner trained on {simulation.SupervisedExampleCount} stored example(s). The dotted curve is the evolving simulated cross-section, and the solid curve is the centered bimodal Gaussian fit where the valley between peaks is the removal signature."
            : $"Polynomial gap-filling fit (degree {simulation.PolynomialDegree}) across {simulation.References.Count} ordered reference stage(s). The dotted curve is the evolving simulated cross-section, and the solid curve is the centered bimodal Gaussian fit where the valley between peaks is the removal signature.";
        SupervisedModelStatusText = _supervisedGrowthLearning.DescribeModel(_supervisedGrowthModel);
    }

    private SurfaceSimulationResult? GetOrBuildSimulationCache()
    {
        if (_surfaceSimulationCache is not null) return _surfaceSimulationCache;
        if (SimulationStartFile is null || SimulationEndFile is null || ReferenceEquals(SimulationStartFile, SimulationEndFile)) return null;
        _surfaceSimulationCache = _analysis.BuildSurfaceSimulation(Files, SimulationStartFile, SimulationEndFile, _supervisedGrowthModel);
        return _surfaceSimulationCache;
    }

    private void InvalidateSimulationCache()
    {
        _surfaceSimulationCache = null;
        SimulationSurfaceBitmap = null;
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationPlotSeries(SurfaceSimulationResult simulation, double[] currentFrame)
    {
        var rawProfile = _analysis.BuildSurfaceCrossSection(currentFrame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        var fittedProfile = _analysis.BuildCenteredBimodalSimulationProfile(currentFrame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        if (fittedProfile.Count == 0 && rawProfile.Count == 0) return Array.Empty<PolylineSeries>();

        var series = new List<PolylineSeries>(2);
        if (rawProfile.Count > 0)
        {
            series.Add(new PolylineSeries(rawProfile.ToArray(), "#7ed9ff", 2.2, 0.95, Dotted: true));
        }

        if (fittedProfile.Count > 0)
        {
            series.Add(new PolylineSeries(fittedProfile.ToArray(), "#fff4d8", 2.7, 1.0));
        }

        return series;
    }

    private double GetSimulationPlotYMax(SurfaceSimulationResult simulation)
    {
        var maxY = simulation.Frames
            .SelectMany(frame => GetSimulationPlotProfiles(simulation, frame))
            .SelectMany(profile => profile)
            .Where(point => double.IsFinite(point.Y))
            .Select(point => point.Y)
            .DefaultIfEmpty(1)
            .Max();

        return Math.Max(1, maxY * 1.08);
    }

    private IEnumerable<IReadOnlyList<PlotPoint>> GetSimulationPlotProfiles(SurfaceSimulationResult simulation, double[] frame)
    {
        var rawProfile = _analysis.BuildSurfaceCrossSection(frame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        if (rawProfile.Count > 0)
        {
            yield return rawProfile;
        }

        var fittedProfile = _analysis.BuildCenteredBimodalSimulationProfile(frame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        if (fittedProfile.Count > 0)
        {
            yield return fittedProfile;
        }
    }

    private static string BuildSimulationReferenceSummary(SurfaceSimulationResult simulation)
    {
        if (simulation.References.Count == 0) return "Ordered references: -";
        var parts = simulation.References
            .Select(reference => $"#{reference.SequenceOrder} {ToStageLabel(reference.Stage)}");
        return $"Ordered reference stages used in the fit: {string.Join("  ->  ", parts)}";
    }

    private static string ToStageLabel(string stage) => stage switch
    {
        "early" => "Early",
        "middle" => "Middle",
        "late" => "Late",
        _ => string.IsNullOrWhiteSpace(stage) ? "Unassigned" : char.ToUpperInvariant(stage[0]) + stage[1..]
    };

    private void EnsureSimulationReferences()
    {
        if (Files.Count == 0)
        {
            StopSimulationPlayback();
            SimulationStartFile = null;
            SimulationEndFile = null;
            SimulationSurfaceBitmap = null;
            return;
        }

        if (SimulationStartFile is null || !Files.Contains(SimulationStartFile))
        {
            SimulationStartFile = Files
                .OrderBy(f => f.SequenceOrder)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => f.HeightData.Length > 0)
                ?? Files.First();
        }

        if (SimulationEndFile is null || !Files.Contains(SimulationEndFile) || ReferenceEquals(SimulationEndFile, SimulationStartFile))
        {
            SimulationEndFile = Files
                .OrderByDescending(f => f.SequenceOrder)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => !ReferenceEquals(f, SimulationStartFile) && f.HeightData.Length > 0)
                ?? SimulationStartFile;
        }
    }

    private void StopSimulationPlayback()
    {
        if (_simulationTimer.IsEnabled) _simulationTimer.Stop();
        IsSimulationPlaying = false;
    }

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        if (SimulationProgress >= 1)
        {
            StopSimulationPlayback();
            PersistSessionIfPossible();
            return;
        }

        SimulationProgress = Math.Min(1, SimulationProgress + 0.04);
        if (SimulationProgress >= 1)
        {
            StopSimulationPlayback();
            PersistSessionIfPossible();
        }
    }

    private static void AppendBoxPlotRow(StringBuilder sb, string measurement, BoxPlotDataset dataset)
    {
        var stats = dataset.Stats;
        sb.AppendLine(string.Join(",",
            measurement,
            dataset.SequenceOrder.ToString(CultureInfo.InvariantCulture),
            Csv(dataset.Stage),
            Csv(dataset.FileName),
            stats.Count.ToString(CultureInfo.InvariantCulture),
            stats.Mean.ToString("F4", CultureInfo.InvariantCulture),
            stats.Median.ToString("F4", CultureInfo.InvariantCulture),
            stats.Q1.ToString("F4", CultureInfo.InvariantCulture),
            stats.Q3.ToString("F4", CultureInfo.InvariantCulture),
            stats.WhiskerLow.ToString("F4", CultureInfo.InvariantCulture),
            stats.WhiskerHigh.ToString("F4", CultureInfo.InvariantCulture),
            stats.StandardError.ToString("F4", CultureInfo.InvariantCulture),
            stats.StandardDeviation.ToString("F4", CultureInfo.InvariantCulture)));
    }

    private static void AppendEquationCurvesCsv(StringBuilder sb, IReadOnlyList<EquationDiscoveryCurve> curves)
    {
        if (curves.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("curve_label,stage,kind,tau,x,y");
        foreach (var curve in curves)
        {
            foreach (var point in curve.Points)
            {
                sb.AppendLine(string.Join(",",
                    Csv(curve.Label),
                    Csv(curve.Stage),
                    Csv(curve.Kind),
                    curve.Tau.ToString("F6", CultureInfo.InvariantCulture),
                    point.X.ToString("F6", CultureInfo.InvariantCulture),
                    point.Y.ToString("F6", CultureInfo.InvariantCulture)));
            }
        }
    }

    private static PlotPoint[] ToRelativePercentPoints(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Array.Empty<PlotPoint>();
        if (values.Count == 1) return new[] { new PlotPoint(0, values[0]) };
        return values.Select((v, i) => new PlotPoint(i * 100.0 / (values.Count - 1), v)).ToArray();
    }

    private void PersistSessionIfPossible()
    {
        if (_suspendSessionPersistence) return;
        _sessionPersistence.Save(BuildSessionSnapshot());
    }

    private SessionSnapshot BuildSessionSnapshot()
    {
        return new SessionSnapshot
        {
            SelectedTabIndex = SelectedTabIndex,
            EvolutionProgress = EvolutionProgress,
            SimulationProgress = SimulationProgress,
            SelectedFilePath = SelectedFile?.FilePath,
            SimulationStartFilePath = SimulationStartFile?.FilePath,
            SimulationEndFilePath = SimulationEndFile?.FilePath,
            Files = Files.Select(file => new FileSessionSnapshot
            {
                FilePath = file.FilePath,
                Stage = file.Stage,
                ConditionType = file.ConditionType,
                AntibioticDoseUgPerMl = file.AntibioticDoseUgPerMl,
                SequenceOrder = file.SequenceOrder,
                GuideCorridorWidthNm = file.GuideCorridorWidthNm,
                DisplayRangeMode = file.DisplayRangeMode,
                FixedDisplayMin = file.FixedDisplayMin,
                FixedDisplayMax = file.FixedDisplayMax,
                GuideLineFinished = file.GuideLineFinished,
                GuidePoints = file.GuidePoints.Select(PointSnapshot.From).ToList(),
                ProfileLine = file.ProfileLine.Select(PointSnapshot.From).ToList()
            }).ToList()
        };
    }
}
