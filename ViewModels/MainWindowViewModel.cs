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
    private readonly PiecrustAnalysisService _analysis = new();
    private readonly SessionPersistenceService _sessionPersistence = new();
    private readonly DispatcherTimer _simulationTimer;
    private SurfaceSimulationResult? _surfaceSimulationCache;
    private bool _suspendSessionPersistence;

    public IReadOnlyList<string> ConditionOptions { get; } = new[] { "unassigned", "control", "treated" };
    public IReadOnlyList<string> StageOptions { get; } = new[] { "early", "middle", "late" };
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
    [ObservableProperty] private IReadOnlyList<StageSummaryRow> stageSummaries = Array.Empty<StageSummaryRow>();
    [ObservableProperty] private IReadOnlyList<GrowthQuantificationRow> growthRows = Array.Empty<GrowthQuantificationRow>();
    [ObservableProperty] private GrowthQuantificationRow? currentGrowthRow;
    [ObservableProperty] private string selectedFileNameText = "No file selected";
    [ObservableProperty] private string selectedDisplayMinText = "Display Min: -";
    [ObservableProperty] private string selectedDisplayMaxText = "Display Max: -";
    [ObservableProperty] private string selectedMeanHeightText = "Mean Height: run guided extraction";
    [ObservableProperty] private string selectedMeanWidthText = "Mean Width: run guided extraction";
    [ObservableProperty] private string selectedContinuityText = "Continuity: -";
    [ObservableProperty] private string selectedPeakSeparationText = "Peak Separation: -";
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
    [ObservableProperty] private string simulationReferenceSummaryText = "Ordered references: -";
    [ObservableProperty] private string simulationSurfaceMetaText = "Surface frame: -";
    [ObservableProperty] private string simulationSurfaceXAxisLabel = "Aligned x [nm]";
    [ObservableProperty] private string simulationSurfaceYAxisLabel = "Aligned y [nm]";
    [ObservableProperty] private WriteableBitmap? simulationSurfaceBitmap;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> simulationSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private double simulationPlotFixedYMin = double.NaN;
    [ObservableProperty] private double simulationPlotFixedYMax = double.NaN;

    public MainWindowViewModel()
    {
        _simulationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _simulationTimer.Tick += OnSimulationTick;
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
                file.GuideLineFinished = saved.GuideLineFinished;
                file.GuidePoints.Clear();
                foreach (var point in saved.GuidePoints) file.GuidePoints.Add(point.ToPointD());
                file.ProfileLine.Clear();
                foreach (var point in saved.ProfileLine) file.ProfileLine.Add(point.ToPointD());
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

                var preview = _previewBitmapService.Render(loaded.Data, loaded.Width, loaded.Height, scientificPreview: loaded.PreferScientificPreview);
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
                    PixelWidth = loaded.Width,
                    PixelHeight = loaded.Height,
                    ScanSizeNm = loaded.ScanSizeNm,
                    NmPerPixel = loaded.NmPerPixel,
                    Unit = loaded.Unit,
                    DisplayMin = preview.Min,
                    DisplayMax = preview.Max,
                    PreviewBitmap = preview.Bitmap,
                    Stage = stage,
                    SequenceOrder = Files.Count + 1,
                    HeightData = loaded.Data,
                    GuideCorridorWidthNm = Math.Max(8, loaded.NmPerPixel * 12)
                };
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
        sb.AppendLine("file,stage,condition,dose_ug_per_ml,mean_height_nm,height_sem_nm,mean_width_nm,width_sem_nm,continuity,roughness_nm,peak_separation_nm,dip_depth_nm");
        if (SelectedFile.GuidedSummary is { } summary)
        {
            sb.AppendLine(string.Join(",",
                Csv(SelectedFile.Name),
                Csv(SelectedFile.Stage),
                Csv(SelectedFile.ConditionType),
                SelectedFile.AntibioticDoseUgPerMl.ToString("F4"),
                summary.MeanHeightNm.ToString("F4"),
                summary.HeightSemNm.ToString("F4"),
                summary.MeanWidthNm.ToString("F4"),
                summary.WidthSemNm.ToString("F4"),
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
            sb.AppendLine("stage,image_count,height_mean_nm,height_std_nm,width_mean_nm,width_std_nm");
            foreach (var row in StageSummaries)
            {
                sb.AppendLine(string.Join(",",
                    Csv(row.Stage),
                    row.ImageCount.ToString(CultureInfo.InvariantCulture),
                    row.HeightMeanNm.ToString("F4"),
                    row.HeightStdNm.ToString("F4"),
                    row.WidthMeanNm.ToString("F4"),
                    row.WidthStdNm.ToString("F4")));
            }

            sb.AppendLine();
        }
        sb.AppendLine("measurement,stage,count,mean,median,q1,q3,whisker_low,whisker_high,sem,stddev");
        foreach (var dataset in HeightBoxPlots)
        {
            AppendBoxPlotRow(sb, "height_nm", dataset);
        }
        foreach (var dataset in WidthBoxPlots)
        {
            AppendBoxPlotRow(sb, "width_nm", dataset);
        }
        return sb.ToString();
    }

    public string BuildGrowthQuantificationCsv()
    {
        if (GrowthRows.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("file,stage,condition,dose_ug_per_ml,mean_height_nm,height_sem_nm,mean_width_nm,width_sem_nm,addition_rate_nm,removal_rate_nm,raw_compromise_ratio,compromise_ratio,control_profile_deviation,control_reference_count");
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

    partial void OnSelectedFileChanged(PiecrustFileState? value)
    {
        SyncSelectedFileDisplayMetrics();
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
        StageSummaries = _analysis.BuildStageSummaries(Files).ToArray();
        GrowthRows = Files.Select(f => _analysis.BuildGrowthQuantification(Files, f)).Where(r => r is not null).Cast<GrowthQuantificationRow>().ToArray();
        CurrentGrowthRow = SelectedFile is null ? null : _analysis.BuildGrowthQuantification(Files, SelectedFile);
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

    public void RefreshAfterSelectionEdit()
    {
        SyncSelectedFileDisplayMetrics();
        RefreshDerivedState();
        PersistSessionIfPossible();
    }

    private void RefreshSelectedSummaryText()
    {
        if (SelectedFile is null)
        {
            SelectedFileNameText = "No file selected";
            SelectedDisplayMinText = "Display Min: -";
            SelectedDisplayMaxText = "Display Max: -";
            SelectedMeanHeightText = "Mean Height: run guided extraction";
            SelectedMeanWidthText = "Mean Width: run guided extraction";
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
        SelectedDisplayMinText = $"Display Min: {SelectedFile.DisplayMin:F2}";
        SelectedDisplayMaxText = $"Display Max: {SelectedFile.DisplayMax:F2}";
        CurrentProfileXAxisLabel = $"x [{SelectedFile.Unit}]";
        CurrentProfileYAxisLabel = $"y [{SelectedFile.Unit}]";
        EvolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
        EvolutionYAxisLabel = $"Baseline-shifted height [{SelectedFile.Unit}]";
        SimulationXAxisLabel = $"Aligned x [{SelectedFile.Unit}]";
        SimulationYAxisLabel = $"Simulated height [{SelectedFile.Unit}]";
        SimulationSurfaceXAxisLabel = $"Aligned x [{SelectedFile.Unit}]";
        SimulationSurfaceYAxisLabel = $"Aligned y [{SelectedFile.Unit}]";
        SelectedStageHintText = $"Stage is currently '{SelectedFile.Stage}'. You can override the auto-assigned stage if this file belongs in a different phase.";

        if (SelectedFile.GuidedSummary is { } summary)
        {
            SelectedMeanHeightText = $"Mean Height: {summary.MeanHeightNm:F2} nm";
            SelectedMeanWidthText = $"Mean Width: {summary.MeanWidthNm:F2} nm";
            SelectedContinuityText = $"Continuity: {summary.Continuity:F3}";
            SelectedPeakSeparationText = $"Peak Separation: {summary.PeakSeparationNm:F2} nm";
        }
        else
        {
            SelectedMeanHeightText = "Mean Height: run guided extraction";
            SelectedMeanWidthText = "Mean Width: run guided extraction";
            SelectedContinuityText = "Continuity: -";
            SelectedPeakSeparationText = "Peak Separation: -";
        }

        if (CurrentGrowthRow is { } growth)
        {
            CurrentAdditionRateText = $"Addition Rate: {growth.AdditionRateNm:F2} nm";
            CurrentRemovalRateText = $"Removal Rate: {growth.RemovalRateNm:F2} nm";
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
            return;
        }

        var currentFrame = _analysis.BuildInterpolatedSimulationFrame(simulation, SimulationProgress);
        var rendered = _previewBitmapService.Render(currentFrame, simulation.Width, simulation.Height, simulation.DisplayMin, simulation.DisplayMax, scientificPreview: false);
        SimulationSurfaceBitmap = rendered.Bitmap;
        SimulationSeries = BuildSimulationPlotSeries(simulation, currentFrame);
        var simulationMax = simulation.Frames.Count == 0
            ? 1
            : simulation.Frames
                .Select(frame => _analysis.BuildCenteredBimodalSimulationProfile(frame, simulation.Width, simulation.Height, simulation.ScanSizeNmX))
                .Where(profile => profile.Count > 0)
                .Select(profile => profile.Max(point => point.Y))
                .DefaultIfEmpty(1)
                .Max();
        SimulationPlotFixedYMin = 0;
        SimulationPlotFixedYMax = Math.Max(1, simulationMax * 1.05);
        SimulationReferenceSummaryText = BuildSimulationReferenceSummary(simulation);
        var alignmentText = simulation.UsesGuidedAlignment
            ? "Full images were rotated and centered from the guided reference direction before fitting."
            : "Guided alignment was unavailable for one or more references, so full-image surfaces were used.";
        SimulationSurfaceMetaText = $"2D surface frame {(int)Math.Round(SimulationProgress * (simulation.Frames.Count - 1)) + 1}/{simulation.Frames.Count}  |  x: 0-{simulation.ScanSizeNmX:F1} {simulation.Unit}  |  y: 0-{simulation.ScanSizeNmY:F1} {simulation.Unit}  |  {alignmentText}";
        SimulationStatusText = $"Polynomial gap-filling fit (degree {simulation.PolynomialDegree}) across {simulation.References.Count} ordered reference stage(s), displayed as a centered bimodal Gaussian cross-section where the valley between peaks is the removal signature.";
    }

    private SurfaceSimulationResult? GetOrBuildSimulationCache()
    {
        if (_surfaceSimulationCache is not null) return _surfaceSimulationCache;
        if (SimulationStartFile is null || SimulationEndFile is null || ReferenceEquals(SimulationStartFile, SimulationEndFile)) return null;
        _surfaceSimulationCache = _analysis.BuildSurfaceSimulation(Files, SimulationStartFile, SimulationEndFile);
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
            series.Add(new PolylineSeries(rawProfile.ToArray(), "#d9ae72", 1.3, 0.35, Dashed: true));
        }

        if (fittedProfile.Count > 0)
        {
            series.Add(new PolylineSeries(fittedProfile.ToArray(), "#fff4d8", 2.7, 1.0));
        }

        return series;
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
            Csv(dataset.Label),
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
                GuideLineFinished = file.GuideLineFinished,
                GuidePoints = file.GuidePoints.Select(PointSnapshot.From).ToList(),
                ProfileLine = file.ProfileLine.Select(PointSnapshot.From).ToList()
            }).ToList()
        };
    }
}
