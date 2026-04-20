using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PiecrustAnalyser.CSharp.Models;
using PiecrustAnalyser.CSharp.Services;

namespace PiecrustAnalyser.CSharp.ViewModels;

public sealed class ActivityLogEntry
{
    public required string Level { get; init; }
    public required string TimestampText { get; init; }
    public required string Message { get; init; }
    public required string AccentHex { get; init; }
}

public sealed class UserAlertRequestedEventArgs : EventArgs
{
    public required string Title { get; init; }
    public required string Message { get; init; }
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double GoodEquationRmseThresholdNm = 30.0;
    private const double FailEquationRmseThresholdNm = 50.0;
    private const int MaxVisibleLogEntries = 60;
    private const int GrowthModelTabIndex = 2;
    private const int EquationDiscoveryTabIndex = 3;
    private readonly FileLoadingService _fileLoading = new();
    private readonly PreviewBitmapService _previewBitmapService = new();
    private readonly HeightMapDisplayService _heightMapDisplay = new();
    private readonly PiecrustAnalysisService _analysis = new();
    private readonly SupervisedGrowthLearningService _supervisedGrowthLearning = new();
    private readonly EquationDiscoveryService _equationDiscovery = new();
    private readonly SessionPersistenceService _sessionPersistence = new();
    private readonly DispatcherTimer _simulationTimer;
    private readonly DispatcherTimer _equationPlaybackTimer;
    private SurfaceSimulationResult? _surfaceSimulationCache;
    private SupervisedGrowthModel? _supervisedGrowthModel;
    private EquationDiscoveryResult? _equationDiscoveryResult;
    private IReadOnlyList<SimulationEquationCandidate> _simulationEquationCandidates = Array.Empty<SimulationEquationCandidate>();
    private SimulationPlaybackModel? _subscribedPlaybackModel;
    private bool _suspendSessionPersistence;
    private bool _syncingSelectedDisplayControls;
    private bool _isApplyingAutomaticSequenceOrdering;
    private string? _lastLoggedStatusText;

    public IReadOnlyList<string> ConditionOptions { get; } = new[] { "unassigned", "control", "treated" };
    public IReadOnlyList<string> StageOptions { get; } = new[] { "none", "early", "middle", "late" };
    public IReadOnlyList<string> SequencingModeOptions { get; } = new[] { "auto", "manual" };
    public IReadOnlyList<string> GrowthModelModeOptions { get; } = new[] { "Current Free Model", "Constant Separation", "Constant Peak Width", "Amplitude Only" };
    public IReadOnlyList<string> DisplayModeOptions { get; } = new[] { "auto", "full", "fixed" };
    public ObservableCollection<PiecrustFileState> Files { get; } = new();
    public ObservableCollection<ActivityLogEntry> ActivityLogs { get; } = new();
    public string SessionLogPath { get; } = Path.Combine(Path.GetTempPath(), "piecrust-analyser-csharp-session.log");
    public string SessionLogCaption => $"Session log: {Path.GetFileName(SessionLogPath)}";
    public event EventHandler<UserAlertRequestedEventArgs>? UserAlertRequested;

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
    [ObservableProperty] private string currentProfileYAxisLabel = "Raw height [nm]";
    [ObservableProperty] private string evolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
    [ObservableProperty] private string evolutionYAxisLabel = "Height above local baseline [nm]";
    [ObservableProperty] private string selectedStageHintText = "Stage is auto-classified on load, but you can change it manually.";
    [ObservableProperty] private string selectedSequencingMode = "auto";
    [ObservableProperty] private bool isManualSequencing;
    [ObservableProperty] private string currentCorridorWidthOverlayText = "Corridor: -";
    [ObservableProperty] private PiecrustFileState? simulationStartFile;
    [ObservableProperty] private PiecrustFileState? simulationEndFile;
    [ObservableProperty] private string selectedGrowthModelMode = "Current Free Model";
    [ObservableProperty] private double simulationProgress;
    [ObservableProperty] private bool isSimulationPlaying;
    [ObservableProperty] private string simulationXAxisLabel = "Centered x [nm]";
    [ObservableProperty] private string simulationYAxisLabel = "Height above local baseline [nm]";
    [ObservableProperty] private string simulationStatusText = "Select start and end reference files to run the full 2D growth simulation.";
    [ObservableProperty] private string supervisedModelStatusText = "Supervised ML status: no learned examples yet.";
    [ObservableProperty] private string simulationReferenceSummaryText = "Ordered references: -";
    [ObservableProperty] private string simulationSurfaceMetaText = "Surface frame: -";
    [ObservableProperty] private string simulationSurfaceXAxisLabel = "Centered x [nm]";
    [ObservableProperty] private string simulationSurfaceYAxisLabel = "Aligned y [nm]";
    [ObservableProperty] private WriteableBitmap? simulationSurfaceBitmap;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> simulationSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private string simulationPlotLegendText = "Dotted = centered evolving cross-section | Solid = polynomial-in-time bimodal Gaussian fit";
    [ObservableProperty] private double simulationPlotFixedXMin = double.NaN;
    [ObservableProperty] private double simulationPlotFixedXMax = double.NaN;
    [ObservableProperty] private double simulationPlotFixedYMin = double.NaN;
    [ObservableProperty] private double simulationPlotFixedYMax = double.NaN;
    [ObservableProperty] private string equationDiscoveryStatusText = "Run Growth Model first, then discover growth-model-derived bimodal equations from the current centered simulation.";
    [ObservableProperty] private string equationDiscoveryMetaText = "Equation Discovery is now sourced from the active Growth Model simulation, so sequence order and centered bimodal reconstruction stay consistent across both tabs.";
    [ObservableProperty] private string equationDiscoveryStageMappingText = "Sequence anchors: -";
    [ObservableProperty] private string equationDiscoveryProfileModeText = "Current visual model: Equation Discovery inherits the centered bimodal Gaussian growth law already fitted in Growth Model, then ranks alternate polynomial orders for the same left/right peak amplitudes, widths, and separation.";
    [ObservableProperty] private string equationDiscoveryOverlayLegendText = "Colours follow sequence-derived pseudo-time from green to red. Solid lines = centered observed growth-model reference profiles. Dashed lines = the selected bimodal reconstruction at the same ordered anchors.";
    [ObservableProperty] private string equationDiscoveryProgressionLegendText = "Bimodal progression over sequence-derived pseudo-time: each curve is reconstructed from the selected growth-model-derived left/right peak amplitudes, widths, and separation.";
    [ObservableProperty] private string equationDiscoveryDiagnosticsLegendText = "Residual diagnostics plot observed minus reconstructed height on the same centered -90 to 90 nm axis. Curves closer to 0 nm indicate a better fit.";
    [ObservableProperty] private string equationDiagnosticsSummaryText = "Fit diagnostics will appear here after equation discovery.";
    [ObservableProperty] private string equationDiscoveryTermGuideText = "Term guide covers the growth-model-derived bimodal Gaussian system used for simulation playback, ranked candidate comparison, and reconstruction diagnostics.";
    [ObservableProperty] private string equationDiscoveryXAxisLabel = "Perpendicular offset from guide centre z [nm]";
    [ObservableProperty] private string equationDiscoveryYAxisLabel = "Height above local baseline z [nm]";
    [ObservableProperty] private IReadOnlyList<EquationDiscoveryStageProfile> equationDiscoveryStageProfiles = Array.Empty<EquationDiscoveryStageProfile>();
    [ObservableProperty] private IReadOnlyList<EquationTermExplanation> equationTermExplanations = Array.Empty<EquationTermExplanation>();
    [ObservableProperty] private IReadOnlyList<EquationCandidateResult> equationFamily = Array.Empty<EquationCandidateResult>();
    [ObservableProperty] private EquationCandidateResult? selectedEquationCandidate;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationOverlaySeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationProgressionSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationDiagnosticsSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private double equationDiscoveryFixedXMin = -90.0;
    [ObservableProperty] private double equationDiscoveryFixedXMax = 90.0;
    [ObservableProperty] private SimulationPlaybackModel? equationSimulationPlayback;
    [ObservableProperty] private IReadOnlyList<PolylineSeries> equationPlaybackSeries = Array.Empty<PolylineSeries>();
    [ObservableProperty] private double equationPlaybackFrameMaximum = 1;
    [ObservableProperty] private double equationPlaybackFramePosition;
    [ObservableProperty] private string equationPlaybackStatusText = "Run Growth Model and then Equation Discovery to enable playback.";
    [ObservableProperty] private string equationPlaybackTauText = "Tau: -";
    [ObservableProperty] private string equationPlaybackHeightText = "Height: -";
    [ObservableProperty] private string equationPlaybackWidthText = "Width: -";
    [ObservableProperty] private double equationPlaybackFixedXMin = double.NaN;
    [ObservableProperty] private double equationPlaybackFixedXMax = double.NaN;
    [ObservableProperty] private double equationPlaybackFixedYMin = double.NaN;
    [ObservableProperty] private double equationPlaybackFixedYMax = double.NaN;

    public MainWindowViewModel()
    {
        _simulationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _simulationTimer.Tick += OnSimulationTick;
        _equationPlaybackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _equationPlaybackTimer.Tick += OnEquationPlaybackTick;
        AddLogEntry("info", "Pie Crust Analyser session started.");
    }

    private sealed class SimulationEquationCandidate
    {
        public required EquationCandidateResult Display { get; init; }
        public required int Degree { get; init; }
        public bool IsExactGrowthModel { get; init; }
        public required double[] LeftAmplitudeCoefficients { get; init; }
        public required double[] LeftSigmaCoefficients { get; init; }
        public required double[] RightAmplitudeCoefficients { get; init; }
        public required double[] RightSigmaCoefficients { get; init; }
        public required double[] SeparationCoefficients { get; init; }
    }

    private void AddLogEntry(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
        var accent = normalizedLevel switch
        {
            "error" => "#d97f7f",
            "warning" => "#f0c978",
            "status" => "#d8b07a",
            _ => "#bca37d"
        };

        ActivityLogs.Insert(0, new ActivityLogEntry
        {
            Level = normalizedLevel.ToUpperInvariant(),
            TimestampText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Message = message.Trim(),
            AccentHex = accent
        });
        while (ActivityLogs.Count > MaxVisibleLogEntries)
        {
            ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
        }

        try
        {
            File.AppendAllText(
                SessionLogPath,
                $"[{DateTime.Now:O}] {normalizedLevel.ToUpperInvariant()}: {message.Trim()}{Environment.NewLine}");
        }
        catch
        {
            // Keep the UI responsive even if session log persistence fails.
        }
    }

    private void RaiseUserAlert(string title, string message)
    {
        AddLogEntry("error", message);
        StatusText = message;
        UserAlertRequested?.Invoke(this, new UserAlertRequestedEventArgs
        {
            Title = title,
            Message = message
        });
    }

    public void ReportRecoverableError(string title, string message) => RaiseUserAlert(title, message);

    private bool EnsureSelectedFile(string actionName)
    {
        if (SelectedFile is not null) return true;
        RaiseUserAlert(
            "No File Selected",
            $"{actionName} needs a selected file first. Load or pick a file from the Files list, then try again.");
        return false;
    }

    private static bool HasUsableGuide(PiecrustFileState? file) =>
        file is not null &&
        file.UseManualGuide &&
        file.GuideLineFinished &&
        file.GuidePoints.Count >= 2 &&
        file.HeightData.Length > 0;

    private bool EnsureGuideReady(string actionName)
    {
        if (!EnsureSelectedFile(actionName)) return false;
        if (HasUsableGuide(SelectedFile)) return true;
        RaiseUserAlert(
            "Guide Required",
            $"{actionName} needs a finished centre line on the selected file. Use Start Centre Line, place at least two guide points, then click Finish Centre Line.");
        return false;
    }

    private bool EnsureDistinctSimulationReferences(string actionName)
    {
        if (SimulationStartFile is null || SimulationEndFile is null)
        {
            RaiseUserAlert(
                "Reference Files Needed",
                $"{actionName} needs both a start reference and an end reference. Choose two ordered files in the Growth Model tab first.");
            return false;
        }

        if (ReferenceEquals(SimulationStartFile, SimulationEndFile))
        {
            RaiseUserAlert(
                "Different References Needed",
                $"{actionName} needs two different reference files so the model has a real progression interval to simulate.");
            return false;
        }

        return true;
    }

    private bool TryBuildEquationDiscoveryInputs(
        out EquationDiscoveryProfileInput[] profileInputs,
        out int[] orderedSequences)
    {
        var missingSequence = Files
            .Where(file => file.SequenceOrder <= 0)
            .Select(file => file.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateSequenceGroups = Files
            .Where(file => file.SequenceOrder > 0)
            .GroupBy(file => file.SequenceOrder)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key)
            .ToArray();
        var preparedInputs = Files
            .Select(file => new
            {
                File = file,
                Input = _analysis.BuildEquationDiscoveryProfileInput(file)
            })
            .ToArray();
        var guidePreparedFailures = preparedInputs
            .Where(item => HasUsableGuide(item.File) && item.Input is null)
            .Select(item => item.File.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        profileInputs = preparedInputs
            .Where(item => item.Input is not null)
            .Select(item => item.Input!)
            .Where(input => input.SequenceOrder > 0)
            .OrderBy(input => input.SequenceOrder)
            .ThenBy(input => input.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        orderedSequences = profileInputs
            .Select(input => input.SequenceOrder)
            .Distinct()
            .OrderBy(sequence => sequence)
            .ToArray();

        var problems = new List<string>();
        if (missingSequence.Length > 0)
        {
            problems.Add("Missing sequence number: " + string.Join(", ", missingSequence));
        }

        if (duplicateSequenceGroups.Length > 0)
        {
            problems.Add("Duplicate sequence anchors: " + string.Join(
                " | ",
                duplicateSequenceGroups.Select(group =>
                    $"#{group.Key} -> {string.Join(", ", group.Select(file => file.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}")));
        }

        if (guidePreparedFailures.Length > 0)
        {
            problems.Add("Guides present but not usable for discovery: " + string.Join(", ", guidePreparedFailures));
        }

        if (profileInputs.Length < 2 || orderedSequences.Length < 2)
        {
            var missingGuides = Files
                .Where(file => !HasUsableGuide(file))
                .Select(file => file.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingGuides.Length > 0)
            {
                problems.Add("Missing or unfinished centre line: " + string.Join(", ", missingGuides));
            }

            problems.Add("Equation discovery needs at least two guided files across two distinct sequence anchors.");
        }

        if (problems.Count == 0) return true;

        RaiseUserAlert(
            "Equation Discovery Setup Incomplete",
            string.Join(Environment.NewLine, problems));
        ClearEquationDiscoveryResults("Equation discovery is waiting for complete guides and sequence anchors.");
        return false;
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
            SelectedSequencingMode = string.IsNullOrWhiteSpace(snapshot.SelectedSequencingMode) ? "auto" : snapshot.SelectedSequencingMode;
            SelectedGrowthModelMode = string.IsNullOrWhiteSpace(snapshot.SelectedGrowthModelMode) ? "Current Free Model" : ToGrowthModelModeLabel(snapshot.SelectedGrowthModelMode);

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

    partial void OnStatusTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (string.Equals(value, _lastLoggedStatusText, StringComparison.Ordinal)) return;
        _lastLoggedStatusText = value;
        AddLogEntry("status", value);
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
        ApplyAutomaticSequenceOrdering(force: true);

        InvalidateSimulationCache();
        RefreshDerivedState();
        StatusText = Files.Count == 0
            ? "No files are loaded to number."
            : "Sequence numbers refreshed from increasing raw guided height, with the original file order used as the tie-break.";
        PersistSessionIfPossible();
    }

    private void ApplyAutomaticSequenceOrdering(bool force = false)
    {
        if (!force && !string.Equals(SelectedSequencingMode, "auto", StringComparison.OrdinalIgnoreCase)) return;
        if (_isApplyingAutomaticSequenceOrdering || Files.Count == 0) return;

        var candidates = Files
            .Select((file, index) => new
            {
                File = file,
                OriginalIndex = index,
                RawHeight = file.GuidedSummary?.RawMeanHeightNm ?? double.NaN,
                HasGuidedSummary = file.GuidedSummary is not null
            })
            .ToArray();

        if (!force && candidates.Count(candidate => candidate.HasGuidedSummary) < 2) return;

        var ordered = candidates
            .OrderBy(candidate => candidate.HasGuidedSummary ? 0 : 1)
            .ThenBy(candidate => candidate.HasGuidedSummary ? candidate.RawHeight : double.PositiveInfinity)
            .ThenBy(candidate => candidate.OriginalIndex)
            .ThenBy(candidate => candidate.File.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var changed = false;
        for (var i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].File.SequenceOrder == i + 1) continue;
            changed = true;
            break;
        }

        if (!changed) return;

        _isApplyingAutomaticSequenceOrdering = true;
        try
        {
            for (var i = 0; i < ordered.Length; i++)
            {
                ordered[i].File.SequenceOrder = i + 1;
            }
        }
        finally
        {
            _isApplyingAutomaticSequenceOrdering = false;
        }

        InvalidateSimulationCache();
        ClearEquationDiscoveryResults();
    }

    partial void OnSelectedSequencingModeChanged(string value)
    {
        IsManualSequencing = string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAutomaticSequenceOrdering(force: true);
            StatusText = Files.Count == 0
                ? "Sequencing mode set to auto."
                : "Sequencing mode set to auto. Files are now ordered by increasing raw guided height.";
        }
        else
        {
            StatusText = "Sequencing mode set to manual. You can edit sequence numbers directly.";
        }

        InvalidateSimulationCache();
        ClearEquationDiscoveryResults();
        RefreshDerivedState();
        PersistSessionIfPossible();
    }

    partial void OnSelectedGrowthModelModeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedGrowthModelMode = "Current Free Model";
            return;
        }

        InvalidateSimulationCache();
        ClearEquationDiscoveryResults();
        RefreshSimulationSeries();
        StatusText = $"Growth-model mode set to {ToGrowthModelModeLabel(value)}.";
        PersistSessionIfPossible();
    }

    public void BeginProfileLineSelection()
    {
        if (!EnsureSelectedFile("Line profile marking")) return;
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
        if (!EnsureSelectedFile("Centre-line drawing")) return;
        IsMarkingProfileLine = false;
        IsGuideDrawing = true;
        SelectedFile.GuidePoints.Clear();
        SelectedFile.GuideLineFinished = false;
        StatusText = "Click along the piecrust centre line, then press Finish Centre Line.";
        PersistSessionIfPossible();
    }

    public void FinishGuideLine()
    {
        if (!EnsureSelectedFile("Finishing the centre line")) return;
        SelectedFile.GuideLineFinished = SelectedFile.GuidePoints.Count >= 2;
        IsGuideDrawing = false;
        if (SelectedFile.GuideLineFinished)
        {
            StatusText = "Centre line finished. Run guided extraction next.";
        }
        else
        {
            RaiseUserAlert(
                "More Guide Points Needed",
                "Finish Centre Line needs at least two guide points. Click along the centre line again, then press Finish Centre Line.");
        }
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
        if (!EnsureGuideReady("Guided extraction")) return;
        ClearEquationDiscoveryResults();
        SyncSelectedFileDisplayMetrics();
        SelectedFile.GuidedSummary = _analysis.ExtractGuidedSummary(SelectedFile);
        ApplyAutomaticSequenceOrdering();
        RefreshDerivedState();
        if (SelectedFile.GuidedSummary is null)
        {
            RaiseUserAlert(
                "Guided Extraction Failed",
                $"The guide on {SelectedFile.Name} could not be converted into a usable extraction. Check that the centre line spans the piecrust region and try again.");
        }
        else
        {
            StatusText = $"Guided extraction complete for {SelectedFile.Name}.";
        }
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
        sb.AppendLine($"constraint_mode,{Csv(simulation.ConstraintMode)}");
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

    public Task DiscoverGrowthEquationsAsync()
    {
        if (!EnsureDistinctSimulationReferences("Equation Discovery"))
        {
            SelectedTabIndex = GrowthModelTabIndex;
            ClearEquationDiscoveryResults("Equation discovery is waiting for a valid Growth Model interval.");
            StatusText = EquationDiscoveryStatusText;
            return Task.CompletedTask;
        }

        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || simulation.Frames.Count < 3 || simulation.References.Count < 2)
        {
            RaiseUserAlert(
                "Growth Model Required",
                "Equation Discovery now starts from the current Growth Model result. Run Polynomial Evolution in the Growth Model tab with two ordered guided references first.");
            SelectedTabIndex = GrowthModelTabIndex;
            ClearEquationDiscoveryResults("Equation discovery is waiting for a usable Growth Model simulation.");
            StatusText = EquationDiscoveryStatusText;
            return Task.CompletedTask;
        }

        if (!ApplySimulationAlignedEquationFamilyIfAvailable())
        {
            RaiseUserAlert(
                "Equation Discovery Unavailable",
                "The current Growth Model run did not yield a stable bimodal trajectory for equation ranking. Adjust the reference interval or guides, rerun Growth Model, then try discovery again.");
            SelectedTabIndex = GrowthModelTabIndex;
            ClearEquationDiscoveryResults("Equation discovery could not derive a stable candidate family from the current Growth Model run.");
            StatusText = EquationDiscoveryStatusText;
            return Task.CompletedTask;
        }

        SelectedTabIndex = EquationDiscoveryTabIndex;
        StatusText = $"Equation discovery complete. Showing {EquationFamily.Count} growth-model-derived bimodal candidate equation(s).";
        return Task.CompletedTask;
    }

    public string BuildEquationDiscoveryJson()
    {
        if (_equationDiscoveryResult is not null)
        {
            return _equationDiscoveryResult.RawJson ?? string.Empty;
        }

        if (_simulationEquationCandidates.Count == 0 || EquationFamily.Count == 0) return string.Empty;

        var payload = new
        {
            source = "growth_model",
            status = EquationDiscoveryStatusText,
            meta = EquationDiscoveryMetaText,
            stageMapping = EquationDiscoveryStageProfiles.Select(profile => new
            {
                profile.Stage,
                profile.Tau,
                profile.SampleCount,
                profile.MeanHeightNm,
                profile.HeightStdNm,
                profile.MeanWidthNm,
                profile.WidthStdNm,
                profile.MeanArea,
                profile.MeanRoughnessNm
            }),
            equations = EquationFamily.Select(candidate => new
            {
                candidate.Rank,
                candidate.MethodLabel,
                candidate.DiscoveryMethod,
                candidate.Equation,
                candidate.Confidence,
                candidate.Rmse,
                candidate.PeakHeightError,
                candidate.WidthError,
                candidate.AreaError,
                candidate.StabilityScore,
                candidate.ActiveTerms,
                candidate.Coefficients,
                candidate.Notes
            }),
            diagnostics = new
            {
                summary = EquationDiagnosticsSummaryText,
                legend = EquationDiscoveryDiagnosticsLegendText
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string BuildEquationDiscoveryCsv()
    {
        if (_equationDiscoveryResult is null && EquationFamily.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"sample_id,{Csv(_equationDiscoveryResult?.SampleId ?? $"growth-model-{DateTime.UtcNow:yyyyMMddHHmmss}")}");
        sb.AppendLine($"time_mode,{Csv(_equationDiscoveryResult?.TimeMode ?? "growth_model_sequence_ordered")}");
        sb.AppendLine($"profile_mode,{Csv(_equationDiscoveryResult?.ProfileMode ?? "growth_model_centered_bimodal")}");
        sb.AppendLine($"stage_mapping_mode,{Csv(_equationDiscoveryResult?.StageMappingMode ?? "sequence_order")}");
        sb.AppendLine($"spatial_coordinate_label,{Csv(_equationDiscoveryResult?.SpatialCoordinateLabel ?? EquationDiscoveryXAxisLabel)}");
        sb.AppendLine($"height_label,{Csv(_equationDiscoveryResult?.HeightLabel ?? EquationDiscoveryYAxisLabel)}");
        sb.AppendLine();

        sb.AppendLine("stage,tau");
        if (_equationDiscoveryResult is not null)
        {
            foreach (var entry in _equationDiscoveryResult.StageMapping.OrderBy(entry => entry.Value))
            {
                sb.AppendLine($"{Csv(entry.Key)},{entry.Value.ToString("F4", CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            foreach (var stage in EquationDiscoveryStageProfiles.OrderBy(profile => profile.Tau))
            {
                sb.AppendLine($"{Csv(stage.Stage)},{stage.Tau.ToString("F4", CultureInfo.InvariantCulture)}");
            }
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
        foreach (var stage in (_equationDiscoveryResult?.StageProfiles ?? EquationDiscoveryStageProfiles))
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

        if (_equationDiscoveryResult is not null)
        {
            AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ObservedProfiles);
            AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ReconstructedProfiles);
            AppendEquationCurvesCsv(sb, _equationDiscoveryResult.ProgressionProfiles);
        }

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

    partial void OnEquationPlaybackFramePositionChanged(double value)
    {
        if (EquationSimulationPlayback is null || EquationSimulationPlayback.Profiles.Count == 0) return;
        var clamped = Math.Clamp((int)Math.Round(value), 0, EquationSimulationPlayback.Profiles.Count - 1);
        if (Math.Abs(value - clamped) > 1e-6)
        {
            EquationPlaybackFramePosition = clamped;
            return;
        }

        if (EquationSimulationPlayback.CurrentFrameIndex != clamped)
        {
            EquationSimulationPlayback.CurrentFrameIndex = clamped;
            return;
        }

        RefreshEquationPlaybackDisplay();
    }

    private void RefreshDerivedState()
    {
        ApplyAutomaticSequenceOrdering();
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
            .Select(stage => new EquationDiscoveryStageProfile
            {
                Stage = FormatPseudoTimeAnchorLabel(stage.Stage),
                Tau = stage.Tau,
                SampleCount = stage.SampleCount,
                MeanHeightNm = stage.MeanHeightNm,
                HeightStdNm = stage.HeightStdNm,
                MeanWidthNm = stage.MeanWidthNm,
                WidthStdNm = stage.WidthStdNm,
                MeanArea = stage.MeanArea,
                MeanRoughnessNm = stage.MeanRoughnessNm
            })
            .ToArray();
        EquationFamily = result.EquationFamily
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.StabilityScore)
            .ThenBy(candidate => candidate.Rmse)
            .ThenBy(candidate => candidate.Rank)
            .ToArray();
        EquationDiscoveryStatusText = result.StatusText;
        EquationDiscoveryMetaText = result.StageValidation is { ValidatorAvailable: true } validation
            ? $"{result.MetaModelSummary} Stage validation confidence: {validation.ConfidenceScore:P0} ({validation.Recommendation})."
            : result.MetaModelSummary;
        var tauModeText = result.UseNormalizedTau
            ? "Tau mode: normalized [0, 1]."
            : result.TRange.Count >= 2
                ? $"Tau mode: sequence index range {result.TRange[0]:F2} to {result.TRange[1]:F2}."
                : "Tau mode: raw sequence-index progression.";
        EquationDiscoveryProfileModeText =
            "Current visual model: every playback frame is reconstructed from two Gaussian tramline peaks fitted with a Gaussian-mixture-based profile model. " +
            "Each image contributes 10 equidistant perpendicular guided profiles (corridor width +20%), those 10 profiles are averaged into one representative image profile, and the discovered ODE system evolves A1, A2, D, sigma1, and sigma2 over the inferred progression axis.";
        EquationDiscoveryStageMappingText = "Pseudo-time anchors: " + string.Join("  |  ",
            result.StageMapping
                .OrderBy(entry => entry.Value)
                .Select(entry => $"{FormatPseudoTimeAnchorLabel(entry.Key)} = {entry.Value:F2}")) +
            $"  |  {tauModeText}";
        EquationDiscoveryTermGuideText =
            "Term guide now focuses on the bimodal Gaussian growth system used for playback, ranked candidate comparison, and residual diagnostics.";
        EquationDiscoveryOverlayLegendText =
            "Colours follow sequence-derived pseudo-time from green (earliest anchor) to red (latest anchor). Solid lines = stage-averaged observed AFM profiles z(s). " +
            "Dashed lines = bimodal Gaussian reconstructions evaluated at the matching ordered anchors.";
        EquationDiscoveryProgressionLegendText =
            "Reconstructed progression of h(z, tau): each coloured curve comes directly from the discovered bimodal feature ODE system.";
        EquationDiscoveryDiagnosticsLegendText =
            "Residual diagnostics plot observed minus reconstructed height on the same centered -90 to 90 nm axis. Curves closer to 0 nm indicate a better fit.";
        EquationDiscoveryXAxisLabel = string.IsNullOrWhiteSpace(result.SpatialCoordinateLabel) ? "Aligned centreline position z [nm]" : result.SpatialCoordinateLabel;
        EquationDiscoveryYAxisLabel = string.IsNullOrWhiteSpace(result.HeightLabel) ? "Height above local baseline z [nm]" : result.HeightLabel;
        EquationTermExplanations = BuildEquationTermExplanations(result.EquationFamily);
        EquationOverlaySeries = BuildEquationOverlaySeries(result);
        EquationProgressionSeries = BuildEquationProgressionSeries(result);
        EquationDiagnosticsSeries = BuildEquationDiagnosticsSeries(result);
        EquationDiagnosticsSummaryText = BuildEquationDiagnosticsSummary(result.EquationFamily.FirstOrDefault());
        SelectedEquationCandidate = EquationFamily.FirstOrDefault();
        LoadEquationPlayback(result);
    }

    private void ClearEquationDiscoveryResults(string? status = null)
    {
        StopEquationPlayback();
        SubscribeEquationPlaybackModel(null);
        _equationDiscoveryResult = null;
        _simulationEquationCandidates = Array.Empty<SimulationEquationCandidate>();
        EquationDiscoveryStageProfiles = Array.Empty<EquationDiscoveryStageProfile>();
        EquationFamily = Array.Empty<EquationCandidateResult>();
        SelectedEquationCandidate = null;
        EquationOverlaySeries = Array.Empty<PolylineSeries>();
        EquationProgressionSeries = Array.Empty<PolylineSeries>();
        EquationDiagnosticsSeries = Array.Empty<PolylineSeries>();
        EquationDiagnosticsSummaryText = "Fit diagnostics will appear here after equation discovery.";
        EquationPlaybackSeries = Array.Empty<PolylineSeries>();
        EquationPlaybackFrameMaximum = 1;
        EquationPlaybackFramePosition = 0;
        EquationPlaybackTauText = "Tau: -";
        EquationPlaybackHeightText = "Height: -";
        EquationPlaybackWidthText = "Width: -";
        EquationPlaybackFixedXMin = double.NaN;
        EquationPlaybackFixedXMax = double.NaN;
        EquationPlaybackFixedYMin = double.NaN;
        EquationPlaybackFixedYMax = double.NaN;
        EquationPlaybackStatusText = "Run Growth Model and then Equation Discovery to enable playback.";
        EquationDiscoveryStatusText = status ?? "Run Growth Model first, then discover growth-model-derived bimodal equations from the current centered simulation.";
        EquationDiscoveryMetaText = "Equation Discovery is now sourced from the active Growth Model simulation, so sequence order and centered bimodal reconstruction stay consistent across both tabs.";
        EquationDiscoveryStageMappingText = "Sequence anchors: -";
        EquationDiscoveryProfileModeText = "Current visual model: Equation Discovery inherits the centered bimodal Gaussian growth law already fitted in Growth Model, then ranks alternate polynomial orders for the same left/right peak amplitudes, widths, and separation.";
        EquationDiscoveryOverlayLegendText = "Colours follow sequence-derived pseudo-time from green to red. Solid lines = centered observed growth-model reference profiles. Dashed lines = the selected bimodal reconstruction at the same ordered anchors.";
        EquationDiscoveryProgressionLegendText = "Bimodal progression over sequence-derived pseudo-time: each curve is reconstructed from the selected growth-model-derived left/right peak amplitudes, widths, and separation.";
        EquationDiscoveryDiagnosticsLegendText = "Residual diagnostics plot observed minus reconstructed height on the same centered -90 to 90 nm axis. Curves closer to 0 nm indicate a better fit.";
        EquationDiscoveryTermGuideText = "Bimodal feature guide: A1 and A2 are left/right peak heights, sigma1 and sigma2 are widths, D is peak separation, mu1 and mu2 are the implied centres, and tau is sequence-derived progression rather than real time.";
        EquationDiscoveryXAxisLabel = "Aligned centreline position z [nm]";
        EquationDiscoveryYAxisLabel = "Height above local baseline z [nm]";
        EquationTermExplanations = Array.Empty<EquationTermExplanation>();
    }

    public void ToggleEquationPlayback()
    {
        if (EquationSimulationPlayback is null || EquationSimulationPlayback.Profiles.Count == 0)
        {
            EquationPlaybackStatusText = "No equation playback data is available yet. Run equation discovery first.";
            return;
        }

        if (EquationSimulationPlayback.IsPlaying)
        {
            StopEquationPlayback();
            EquationPlaybackStatusText = "Equation playback paused.";
            RefreshEquationPlaybackDisplay();
            return;
        }

        if (EquationSimulationPlayback.CurrentFrameIndex >= EquationSimulationPlayback.Profiles.Count - 1)
        {
            EquationSimulationPlayback.CurrentFrameIndex = 0;
        }

        EquationSimulationPlayback.IsPlaying = true;
        UpdateEquationPlaybackTimerInterval();
        _equationPlaybackTimer.Start();
        EquationPlaybackStatusText = "Playing discovered-equation playback over the inferred progression axis.";
        RefreshEquationPlaybackDisplay();
    }

    public void ResetEquationPlayback()
    {
        StopEquationPlayback();
        if (EquationSimulationPlayback is null)
        {
            EquationPlaybackStatusText = "No equation playback data is available yet.";
            RefreshEquationPlaybackDisplay();
            return;
        }

        EquationSimulationPlayback.CurrentFrameIndex = 0;
        EquationPlaybackStatusText = "Equation playback reset to the first pseudo-time frame.";
        RefreshEquationPlaybackDisplay();
    }

    private void LoadEquationPlayback(EquationDiscoveryResult result)
    {
        LoadEquationPlaybackPayload(result.SimulationPlayback);
    }

    private void LoadEquationPlaybackPayload(EquationDiscoverySimulationPlayback? playback)
    {
        StopEquationPlayback();
        if (playback is null || !playback.Success || playback.Profiles.Count == 0)
        {
            SubscribeEquationPlaybackModel(null);
            EquationPlaybackFixedXMin = double.NaN;
            EquationPlaybackFixedXMax = double.NaN;
            EquationPlaybackFixedYMin = double.NaN;
            EquationPlaybackFixedYMax = double.NaN;
            EquationPlaybackStatusText = string.IsNullOrWhiteSpace(playback?.Error)
                ? "Equation discovery completed, but no playback payload was returned."
                : $"Equation playback unavailable: {playback.Error}";
            RefreshEquationPlaybackDisplay();
            return;
        }

        var model = new SimulationPlaybackModel
        {
            TauValues = playback.Tau.ToArray(),
            SimulatedHeight = playback.SimulatedHeight.ToArray(),
            SimulatedWidth = playback.SimulatedWidth.ToArray(),
            Profiles = playback.Profiles.ToArray(),
            EnvelopeProfiles = playback.EnvelopeProfiles.ToArray(),
            CurrentFrameIndex = 0,
            PlaybackSpeed = 1.0,
            IsPlaying = false
        };
        SubscribeEquationPlaybackModel(model);
        var allPlaybackPoints = model.Profiles
            .Concat(model.EnvelopeProfiles)
            .SelectMany(curve => curve.Points)
            .ToArray();
        if (allPlaybackPoints.Length > 0)
        {
            EquationPlaybackFixedXMin = allPlaybackPoints.Min(point => point.X);
            EquationPlaybackFixedXMax = allPlaybackPoints.Max(point => point.X);
            EquationPlaybackFixedYMin = 0;
            var maxY = Math.Max(
                playback.SimulatedHeight.DefaultIfEmpty(0).Max(),
                allPlaybackPoints.Max(point => point.Y));
            var padding = Math.Max(0.4, maxY * 0.10);
            EquationPlaybackFixedYMax = maxY + padding;
        }
        else
        {
            EquationPlaybackFixedXMin = double.NaN;
            EquationPlaybackFixedXMax = double.NaN;
            EquationPlaybackFixedYMin = double.NaN;
            EquationPlaybackFixedYMax = double.NaN;
        }
        EquationPlaybackStatusText = string.IsNullOrWhiteSpace(playback.Note)
            ? "Equation playback is ready. Use Play to animate the discovered profile evolution."
            : playback.Note;
        RefreshEquationPlaybackDisplay();
    }

    private void SubscribeEquationPlaybackModel(SimulationPlaybackModel? model)
    {
        if (_subscribedPlaybackModel is not null)
        {
            _subscribedPlaybackModel.PropertyChanged -= OnEquationPlaybackModelPropertyChanged;
        }

        _subscribedPlaybackModel = model;
        EquationSimulationPlayback = model;

        if (_subscribedPlaybackModel is not null)
        {
            _subscribedPlaybackModel.PropertyChanged += OnEquationPlaybackModelPropertyChanged;
        }
    }

    private void OnEquationPlaybackModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SimulationPlaybackModel model) return;
        if (e.PropertyName is nameof(SimulationPlaybackModel.PlaybackSpeed))
        {
            UpdateEquationPlaybackTimerInterval();
        }

        if (e.PropertyName is nameof(SimulationPlaybackModel.CurrentFrameIndex) or nameof(SimulationPlaybackModel.PlaybackSpeed))
        {
            EquationPlaybackFramePosition = model.CurrentFrameIndex;
            RefreshEquationPlaybackDisplay();
        }
    }

    private void OnEquationPlaybackTick(object? sender, EventArgs e)
    {
        if (EquationSimulationPlayback is null || EquationSimulationPlayback.Profiles.Count == 0)
        {
            StopEquationPlayback();
            return;
        }

        if (EquationSimulationPlayback.CurrentFrameIndex < EquationSimulationPlayback.Profiles.Count - 1)
        {
            EquationSimulationPlayback.CurrentFrameIndex++;
            return;
        }

        StopEquationPlayback();
        EquationPlaybackStatusText = "Equation playback finished.";
        RefreshEquationPlaybackDisplay();
    }

    private void StopEquationPlayback()
    {
        if (_equationPlaybackTimer.IsEnabled) _equationPlaybackTimer.Stop();
        if (EquationSimulationPlayback is not null) EquationSimulationPlayback.IsPlaying = false;
    }

    private void UpdateEquationPlaybackTimerInterval()
    {
        var speed = Math.Clamp(EquationSimulationPlayback?.PlaybackSpeed ?? 1.0, 0.1, 3.0);
        _equationPlaybackTimer.Interval = TimeSpan.FromMilliseconds(120.0 / speed);
    }

    private void RefreshEquationPlaybackDisplay()
    {
        if (EquationSimulationPlayback is null || EquationSimulationPlayback.Profiles.Count == 0)
        {
            EquationPlaybackSeries = Array.Empty<PolylineSeries>();
            EquationPlaybackFrameMaximum = 1;
            EquationPlaybackFramePosition = 0;
            EquationPlaybackTauText = "Tau: -";
            EquationPlaybackHeightText = "Height: -";
            EquationPlaybackWidthText = "Width: -";
            return;
        }

        var index = Math.Clamp(EquationSimulationPlayback.CurrentFrameIndex, 0, EquationSimulationPlayback.Profiles.Count - 1);
        if (EquationSimulationPlayback.CurrentFrameIndex != index)
        {
            EquationSimulationPlayback.CurrentFrameIndex = index;
            return;
        }

        EquationPlaybackFrameMaximum = Math.Max(0, EquationSimulationPlayback.Profiles.Count - 1);
        if (Math.Abs(EquationPlaybackFramePosition - index) > 1e-6) EquationPlaybackFramePosition = index;

        var curve = EquationSimulationPlayback.Profiles[index];
        var envelopeCurve = index < EquationSimulationPlayback.EnvelopeProfiles.Count
            ? EquationSimulationPlayback.EnvelopeProfiles[index]
            : null;
        if (curve.Points.Count < 2)
        {
            EquationPlaybackSeries = Array.Empty<PolylineSeries>();
        }
        else
        {
            var playbackSeries = new List<PolylineSeries>();
            if (envelopeCurve is not null && envelopeCurve.Points.Count > 1)
            {
                playbackSeries.Add(new PolylineSeries(
                    envelopeCurve.Points.Select(point => point.ToPlotPoint()).ToArray(),
                    "#8f6a3d",
                    2.0,
                    0.72,
                    Dashed: true));
            }

            playbackSeries.Add(new PolylineSeries(
                curve.Points.Select(point => point.ToPlotPoint()).ToArray(),
                GetPseudoTimeColor(curve.Tau),
                2.9,
                1.0));
            EquationPlaybackSeries = playbackSeries;
        }

        var tau = index < EquationSimulationPlayback.TauValues.Count ? EquationSimulationPlayback.TauValues[index] : curve.Tau;
        var height = index < EquationSimulationPlayback.SimulatedHeight.Count ? EquationSimulationPlayback.SimulatedHeight[index] : 0;
        var width = index < EquationSimulationPlayback.SimulatedWidth.Count ? EquationSimulationPlayback.SimulatedWidth[index] : 0;
        var unit = SelectedFile?.Unit ?? "nm";
        EquationPlaybackTauText = $"Tau: {tau:F2}";
        EquationPlaybackHeightText = $"Height: {height:F2} {unit}";
        EquationPlaybackWidthText = $"Width: {width:F2} {unit}";
    }

    private static IReadOnlyList<PolylineSeries> BuildEquationOverlaySeries(EquationDiscoveryResult result)
    {
        var series = new List<PolylineSeries>();
        foreach (var curve in result.ObservedProfiles.OrderBy(curve => curve.Tau))
        {
            if (curve.Points.Count < 2) continue;
            var color = GetPseudoTimeColor(curve.Tau);
            series.Add(new PolylineSeries(NormalizeEquationCurvePoints(curve.Points), color, 2.6, 0.98));
        }

        foreach (var curve in result.ReconstructedProfiles.OrderBy(curve => curve.Tau))
        {
            if (curve.Points.Count < 2) continue;
            var color = GetPseudoTimeColor(curve.Tau);
            series.Add(new PolylineSeries(NormalizeEquationCurvePoints(curve.Points), color, 2.0, 0.72, Dashed: true));
        }

        return series;
    }

    private static IReadOnlyList<PolylineSeries> BuildEquationProgressionSeries(EquationDiscoveryResult result)
    {
        return result.ProgressionProfiles
            .OrderBy(curve => curve.Tau)
            .Select(curve => new PolylineSeries(
                NormalizeEquationCurvePoints(curve.Points),
                GetPseudoTimeColor(curve.Tau),
                1.9,
                0.98))
            .ToArray();
    }

    private static IReadOnlyList<PolylineSeries> BuildEquationDiagnosticsSeries(EquationDiscoveryResult result)
    {
        var series = new List<PolylineSeries>();
        series.Add(new PolylineSeries(
            [new PlotPoint(-90.0, 0.0), new PlotPoint(90.0, 0.0)],
            "#9a8b78",
            1.2,
            0.55,
            Dashed: true));

        var reconstructedByTau = result.ReconstructedProfiles
            .GroupBy(curve => Math.Round(curve.Tau, 4))
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var observed in result.ObservedProfiles.OrderBy(curve => curve.Tau))
        {
            if (!reconstructedByTau.TryGetValue(Math.Round(observed.Tau, 4), out var reconstructed)) continue;
            var residual = BuildResidualSeries(observed.Points, reconstructed.Points);
            if (residual.Count < 2) continue;
            series.Add(new PolylineSeries(residual, GetPseudoTimeColor(observed.Tau), 2.0, 0.94));
        }

        return series;
    }

    private static string BuildEquationDiagnosticsSummary(EquationCandidateResult? candidate)
    {
        if (candidate is null)
        {
            return "Fit diagnostics will appear here after equation discovery.";
        }

        return
            $"Selected equation diagnostics: confidence {candidate.Confidence:P0}, RMSE {candidate.Rmse:F2} nm, " +
            $"mean peak-height error {candidate.PeakHeightError:F2} nm, mean width error {candidate.WidthError:F2} nm, " +
            $"stability {candidate.StabilityScore:F2}. Residual curves plot observed minus reconstructed height, so traces staying close to 0 nm indicate a more trustworthy fit.";
    }

    private static PlotPoint[] NormalizeEquationCurvePoints(IReadOnlyList<EquationDiscoveryPoint> points, double targetHalfRangeNm = 90.0)
    {
        if (points.Count == 0) return Array.Empty<PlotPoint>();
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var center = (minX + maxX) / 2.0;
        var centered = points.Select(point => new PlotPoint(point.X - center, point.Y)).ToArray();
        var halfSpan = centered.Max(point => Math.Abs(point.X));
        if (!(halfSpan > 1e-9))
        {
            return centered;
        }

        var scale = targetHalfRangeNm / halfSpan;
        return centered
            .Select(point => new PlotPoint(point.X * scale, point.Y))
            .ToArray();
    }

    private static PlotPoint[] NormalizeEquationCurvePoints(IReadOnlyList<PlotPoint> points, double targetHalfRangeNm = 90.0)
    {
        if (points.Count == 0) return Array.Empty<PlotPoint>();
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var center = (minX + maxX) / 2.0;
        var centered = points.Select(point => new PlotPoint(point.X - center, point.Y)).ToArray();
        var halfSpan = centered.Max(point => Math.Abs(point.X));
        if (!(halfSpan > 1e-9))
        {
            return centered;
        }

        var scale = targetHalfRangeNm / halfSpan;
        return centered
            .Select(point => new PlotPoint(point.X * scale, point.Y))
            .ToArray();
    }

    private static Dictionary<string, double> BuildSequencePseudoTimeMapping(IReadOnlyList<int> sequenceOrders)
    {
        var mapping = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (sequenceOrders.Count == 0) return mapping;

        var minSequence = sequenceOrders.Min();
        var maxSequence = sequenceOrders.Max();
        foreach (var sequence in sequenceOrders)
        {
            var tau = maxSequence == minSequence
                ? 0.5
                : (sequence - minSequence) / (double)Math.Max(1, maxSequence - minSequence);
            mapping[BuildSequenceAnchorLabel(sequence)] = tau;
        }

        return mapping;
    }

    private static EquationDiscoveryProfileInput BuildSequenceOrderedEquationDiscoveryInput(EquationDiscoveryProfileInput input)
    {
        return new EquationDiscoveryProfileInput
        {
            FileName = input.FileName,
            FilePath = input.FilePath,
            SequenceOrder = input.SequenceOrder,
            Stage = BuildSequenceAnchorLabel(input.SequenceOrder),
            ConditionType = input.ConditionType,
            Unit = input.Unit,
            DoseUgPerMl = input.DoseUgPerMl,
            ScanSizeNm = input.ScanSizeNm,
            NmPerPixel = input.NmPerPixel,
            MeanHeightNm = input.MeanHeightNm,
            MeanWidthNm = input.MeanWidthNm,
            HeightToWidthRatio = input.HeightToWidthRatio,
            RoughnessNm = input.RoughnessNm,
            PeakSeparationNm = input.PeakSeparationNm,
            DipDepthNm = input.DipDepthNm,
            CompromiseRatio = input.CompromiseRatio,
            XNm = input.XNm,
            YNm = input.YNm,
            SNm = input.SNm,
            ZNm = input.ZNm,
            GuidedPerpendicularProfiles = input.GuidedPerpendicularProfiles
        };
    }

    private static string BuildSequenceAnchorLabel(int sequenceOrder) => $"Sequence {sequenceOrder}";

    private static string FormatPseudoTimeAnchorLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Sequence";
        return string.Join(" ",
            label
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token[1..]));
    }

    private static string GetPseudoTimeColor(double tau)
    {
        var clamped = Math.Clamp(tau, 0.0, 1.0);
        var stops = new[]
        {
            (Tau: 0.0, Color: (R: 0x68, G: 0xa6, B: 0x5b)),
            (Tau: 0.5, Color: (R: 0xd0, G: 0xa7, B: 0x4d)),
            (Tau: 1.0, Color: (R: 0xcf, G: 0x6a, B: 0x4d))
        };

        for (var i = 1; i < stops.Length; i++)
        {
            var a = stops[i - 1];
            var b = stops[i];
            if (clamped > b.Tau) continue;
            var mix = (clamped - a.Tau) / Math.Max(1e-9, b.Tau - a.Tau);
            var r = (int)Math.Round(a.Color.R + (b.Color.R - a.Color.R) * mix);
            var g = (int)Math.Round(a.Color.G + (b.Color.G - a.Color.G) * mix);
            var bl = (int)Math.Round(a.Color.B + (b.Color.B - a.Color.B) * mix);
            return $"#{r:X2}{g:X2}{bl:X2}";
        }

        return "#cf6a4d";
    }

    private bool ApplySimulationAlignedEquationFamilyIfAvailable(EquationDiscoveryResult? result = null)
    {
        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || simulation.Frames.Count < 3) return false;

        var candidates = BuildSimulationEquationCandidates(simulation);
        if (candidates.Count == 0) return false;

        _equationDiscoveryResult = result;
        _simulationEquationCandidates = candidates;
        EquationDiscoveryStageProfiles = BuildSimulationEquationStageProfiles(simulation);
        EquationFamily = candidates.Select(candidate => candidate.Display).ToArray();
        EquationDiscoveryStageMappingText = "Sequence anchors: " + string.Join("  |  ",
            simulation.References
                .OrderBy(reference => reference.Position01)
                .Select(reference => $"{BuildSequenceAnchorLabel(reference.SequenceOrder)} = {reference.Position01:F2}"));
        EquationDiscoveryStatusText = $"Showing the exact Growth Model equation plus {Math.Max(0, candidates.Count - 1)} closest higher-order polynomial approximation(s) in {ToGrowthModelModeLabel(simulation.ConstraintMode)} mode.";
        EquationDiscoveryProfileModeText =
            $"Current reduced model: Equation Discovery is sourced from Growth Model, with z(s, tau) = z_L(s, tau) + z_R(s, tau). Active mode {ToGrowthModelModeLabel(simulation.ConstraintMode)} determines which of A_L, sigma_L, A_R, sigma_R, and Delta are allowed to evolve over sequence-derived tau.";
        var validationText = result?.StageValidation is { ValidatorAvailable: true } validation
            ? $" Stage validation confidence: {validation.ConfidenceScore:P0} ({validation.Recommendation})."
            : string.Empty;
        var metaPrefix = string.IsNullOrWhiteSpace(result?.MetaModelSummary) ? string.Empty : $"{result!.MetaModelSummary} ";
        EquationDiscoveryMetaText =
            metaPrefix +
            $"Sequence order sets the pseudo-time anchors. The first candidate is the exact bimodal trajectory used by Growth Model; the remaining candidates are higher-order polynomial time-evolution fits ranked by closeness to that simulated trajectory. Active mode: {ToGrowthModelModeLabel(simulation.ConstraintMode)}.";
        EquationDiscoveryMetaText += validationText;
        EquationDiscoveryOverlayLegendText =
            "Colours follow sequence-derived pseudo-time from green to red. Solid lines = centered observed growth-model reference profiles. Dashed lines = strict bimodal reconstructions from the selected parameter-evolution equations at those same ordered anchors.";
        EquationDiscoveryProgressionLegendText =
            "Coloured curves show the selected bimodal evolution law. The exact candidate should match the Growth Model solid curve; higher-order candidates show the closest polynomial alternatives to that same simulated trajectory.";
        EquationDiscoveryDiagnosticsLegendText =
            "Residual diagnostics plot observed minus reconstructed height on the same centered -90 to 90 nm axis. Curves closer to 0 nm indicate a better fit.";
        EquationDiscoveryTermGuideText =
            $"Bimodal model guide: z(s, tau) is explicitly written as z_L(s, tau) + z_R(s, tau), so the discovered family remains two-peaked across pseudo-time rather than collapsing to a single Gaussian. Active mode {ToGrowthModelModeLabel(simulation.ConstraintMode)} determines which terms remain constant. A_L and A_R are the left and right peak heights, sigma_L and sigma_R are the corresponding Gaussian widths, Delta is the peak-to-peak spacing, s_c is the fixed centred reference position, and tau is sequence-derived progression rather than real time.";
        EquationDiscoveryXAxisLabel = simulation.UsesGuidedAlignment ? $"Centered corridor offset [{simulation.Unit}]" : $"Centered x [{simulation.Unit}]";
        EquationDiscoveryYAxisLabel = $"Height above local baseline [{simulation.Unit}]";
        EquationTermExplanations = BuildEquationTermExplanations(candidates.Select(candidate => candidate.Display));
        SelectedEquationCandidate = EquationFamily.FirstOrDefault();
        ApplySelectedSimulationEquationCandidate(simulation);
        return true;
    }

    public void SimulateSelectedEquationCandidate()
    {
        var simulation = GetOrBuildSimulationCache();
        if (simulation is null || _simulationEquationCandidates.Count == 0 || SelectedEquationCandidate is null)
        {
            EquationPlaybackStatusText = "No selectable simulation-aligned equation is available yet.";
            return;
        }

        ApplySelectedSimulationEquationCandidate(simulation);
    }

    private void ApplySelectedSimulationEquationCandidate(SurfaceSimulationResult simulation)
    {
        if (SelectedEquationCandidate is null)
        {
            SelectedEquationCandidate = EquationFamily.FirstOrDefault();
        }

        if (SelectedEquationCandidate is null) return;
        var candidate = _simulationEquationCandidates.FirstOrDefault(item =>
            string.Equals(item.Display.DiscoveryMethod, SelectedEquationCandidate.DiscoveryMethod, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            candidate = _simulationEquationCandidates.First();
            SelectedEquationCandidate = candidate.Display;
        }

        EquationOverlaySeries = BuildSimulationEquationOverlaySeries(simulation, candidate);
        EquationProgressionSeries = BuildSimulationEquationProgressionSeries(simulation, candidate);
        EquationDiagnosticsSeries = BuildSimulationEquationDiagnosticsSeries(simulation, candidate);
        EquationDiagnosticsSummaryText = BuildSimulationEquationDiagnosticsSummary(candidate);
        LoadEquationPlaybackPayload(BuildSimulationEquationPlayback(simulation, candidate));
        EquationPlaybackStatusText = $"Simulating {candidate.Display.MethodLabel} (confidence {candidate.Display.Confidence:P0}, RMSE {candidate.Display.Rmse:F2} nm).";
    }

    private EquationDiscoverySimulationPlayback BuildSimulationEquationPlayback(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
        var playbackCurves = new List<EquationDiscoveryCurve>(simulation.FrameProgresses.Count);
        var heights = new List<double>(simulation.FrameProgresses.Count);
        var widths = new List<double>(simulation.FrameProgresses.Count);

        for (var index = 0; index < simulation.FrameProgresses.Count; index++)
        {
            var tau = simulation.FrameProgresses[index];
            var parameters = EvaluateSimulationEquationCandidate(candidate, tau);
            var profile = _analysis.BuildCenteredBimodalProfileFromParameters(parameters, simulation.Width, simulation.ScanSizeNmX);
            if (profile.Count == 0)
            {
                profile = _analysis.BuildBimodalPolynomialSimulationProfile(simulation, tau);
            }
            if (profile.Count == 0 && index < simulation.Frames.Count)
            {
                profile = _analysis.BuildCenteredBimodalSimulationProfile(
                    simulation.Frames[index],
                    simulation.Width,
                    simulation.Height,
                    simulation.ScanSizeNmX);
            }

            playbackCurves.Add(new EquationDiscoveryCurve
            {
                Label = $"Growth-model tau {tau:F2}",
                Stage = "playback",
                Kind = "simulationPlayback",
                Tau = tau,
                Points = profile
                    .Select(point => new EquationDiscoveryPoint { X = point.X, Y = point.Y })
                    .ToArray()
            });
            heights.Add(ComputeProfilePeakHeight(profile));
            widths.Add(ComputeProfileWidthNm(profile));
        }

        return new EquationDiscoverySimulationPlayback
        {
            Success = playbackCurves.Count > 0,
            Tau = simulation.FrameProgresses.ToArray(),
            SimulatedHeight = heights,
            SimulatedWidth = widths,
            Profiles = playbackCurves,
            EnvelopeProfiles = Array.Empty<EquationDiscoveryCurve>(),
            StabilityScore = candidate.Display.StabilityScore,
            Note = "Playback now follows the same strict bimodal Gaussian growth-model equation family shown in the Growth Model tab."
        };
    }

    private IReadOnlyList<EquationDiscoveryStageProfile> BuildSimulationEquationStageProfiles(SurfaceSimulationResult simulation)
    {
        return simulation.References
            .OrderBy(reference => reference.Position01)
            .Select(reference =>
            {
                var file = Files.FirstOrDefault(candidate => string.Equals(candidate.Name, reference.FileName, StringComparison.OrdinalIgnoreCase))
                    ?? Files.FirstOrDefault(candidate => candidate.SequenceOrder == reference.SequenceOrder);
                var summary = file?.GuidedSummary;
                return new EquationDiscoveryStageProfile
                {
                    Stage = BuildSequenceAnchorLabel(reference.SequenceOrder),
                    Tau = reference.Position01,
                    SampleCount = summary?.ValidProfileCount ?? 0,
                    MeanHeightNm = summary?.MeanHeightNm ?? 0,
                    HeightStdNm = summary?.HeightStdNm ?? 0,
                    MeanWidthNm = summary?.MeanWidthNm ?? 0,
                    WidthStdNm = summary?.WidthStdNm ?? 0,
                    MeanArea = summary is null ? 0 : Math.Max(0, summary.MeanHeightNm) * Math.Max(0, summary.MeanWidthNm),
                    MeanRoughnessNm = summary?.RoughnessNm ?? 0
                };
            })
            .ToArray();
    }

    private IReadOnlyList<SimulationEquationCandidate> BuildSimulationEquationCandidates(SurfaceSimulationResult simulation)
    {
        if (simulation.BimodalTrajectory is null) return Array.Empty<SimulationEquationCandidate>();

        var frameRows = new List<(double Tau, double[] Parameters, IReadOnlyList<PlotPoint> Profile)>();
        for (var i = 0; i < simulation.Frames.Count; i++)
        {
            var tau = simulation.FrameProgresses[i];
            var parameters = _analysis.EvaluateSimulationBimodalParameters(simulation.BimodalTrajectory, tau);
            if (parameters.Length < 5) return Array.Empty<SimulationEquationCandidate>();

            var profile = _analysis.BuildBimodalPolynomialSimulationProfile(simulation, tau);
            if (profile.Count < 8) return Array.Empty<SimulationEquationCandidate>();

            frameRows.Add((tau, parameters, profile));
        }

        if (frameRows.Count < 3) return Array.Empty<SimulationEquationCandidate>();

        var exactDegree = simulation.BimodalTrajectory.Degree;
        var maxDegree = Math.Min(8, Math.Max(exactDegree + 3, frameRows.Count - 1));
        var provisional = new List<(SimulationEquationCandidate Candidate, double Score)>();
        var taus = frameRows.Select(row => row.Tau).ToArray();
        var constraintMode = string.IsNullOrWhiteSpace(simulation.ConstraintMode) ? "current" : simulation.ConstraintMode;
        var exactCandidate = BuildExactGrowthModelEquationCandidate(simulation, frameRows, constraintMode);
        if (exactCandidate is not null)
        {
            provisional.Add((exactCandidate, double.NegativeInfinity));
        }

        var approximationDegrees = Enumerable.Range(exactDegree + 1, Math.Max(0, maxDegree - exactDegree)).ToArray();
        if (approximationDegrees.Length == 0)
        {
            approximationDegrees = Enumerable.Range(1, maxDegree)
                .Where(degree => degree != exactDegree)
                .ToArray();
        }

        foreach (var degree in approximationDegrees)
        {
            var leftAmplitude = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[0]).ToArray(), degree);
            var rightAmplitude = _analysis.FitPolynomialCurve(taus, frameRows.Select(row => row.Parameters[2]).ToArray(), degree);
            var leftSigma = FitConstraintAwareCurve(constraintMode, "sigma", taus, frameRows.Select(row => row.Parameters[1]).ToArray(), degree);
            var rightSigma = FitConstraintAwareCurve(constraintMode, "sigma", taus, frameRows.Select(row => row.Parameters[3]).ToArray(), degree);
            var separation = FitConstraintAwareCurve(constraintMode, "separation", taus, frameRows.Select(row => row.Parameters[4]).ToArray(), degree);
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
            var rmseQuality = ComputeEquationRmseQualityFactor(rmse);
            var confidence = Math.Clamp(
                0.35 * stability +
                0.22 * rmseQuality +
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
                MethodLabel = $"{ToGrowthModelModeLabel(constraintMode)} Higher-Order Approximation (order {degree})",
                DiscoveryMethod = $"simulation_{constraintMode}_higher_order_degree_{degree}",
                ActiveTerms = BuildSimulationActiveTerms(constraintMode),
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
                    $"Closest-fit higher-order approximation to the exact centered bimodal Gaussian trajectory used in Growth Model. " +
                    $"Active mode {ToGrowthModelModeLabel(constraintMode)} determines how A_L, σ_L, A_R, σ_R, and Δ are allowed to evolve at pseudo-time τ, while polynomial order {degree} controls the free terms. " +
                    $"RMSE quality: {DescribeEquationRmseQuality(rmse)} (target <= {GoodEquationRmseThresholdNm:F0} nm, fail > {FailEquationRmseThresholdNm:F0} nm)."
            };

            provisional.Add((new SimulationEquationCandidate
            {
                Display = display,
                Degree = degree,
                IsExactGrowthModel = false,
                LeftAmplitudeCoefficients = leftAmplitude,
                LeftSigmaCoefficients = leftSigma,
                RightAmplitudeCoefficients = rightAmplitude,
                RightSigmaCoefficients = rightSigma,
                SeparationCoefficients = separation
            }, rmse + 0.15 * peakError + 0.08 * widthError + 0.04 * areaError - 0.10 * stability - 0.04 * metaPrior));
        }

        return provisional
            .Where(entry => entry.Candidate.IsExactGrowthModel || IsStatisticallyInterpretableEquation(entry.Candidate.Display))
            .OrderByDescending(entry => entry.Candidate.IsExactGrowthModel)
            .ThenBy(entry => entry.Candidate.Display.Rmse)
            .ThenByDescending(entry => entry.Candidate.Display.Confidence)
            .ThenByDescending(entry => entry.Candidate.Display.StabilityScore)
            .ThenBy(entry => entry.Score)
            .GroupBy(entry => BuildEquationCandidateSignature(entry.Candidate.Display), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(4)
            .Select((entry, index) => new SimulationEquationCandidate
            {
                Display = new EquationCandidateResult
                {
                    Rank = index + 1,
                    Equation = entry.Candidate.Display.Equation,
                    MethodLabel = entry.Candidate.Display.MethodLabel,
                    DiscoveryMethod = entry.Candidate.Display.DiscoveryMethod,
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
                IsExactGrowthModel = entry.Candidate.IsExactGrowthModel,
                LeftAmplitudeCoefficients = entry.Candidate.LeftAmplitudeCoefficients,
                LeftSigmaCoefficients = entry.Candidate.LeftSigmaCoefficients,
                RightAmplitudeCoefficients = entry.Candidate.RightAmplitudeCoefficients,
                RightSigmaCoefficients = entry.Candidate.RightSigmaCoefficients,
                SeparationCoefficients = entry.Candidate.SeparationCoefficients
            })
            .ToArray();
    }

    private SimulationEquationCandidate? BuildExactGrowthModelEquationCandidate(
        SurfaceSimulationResult simulation,
        IReadOnlyList<(double Tau, double[] Parameters, IReadOnlyList<PlotPoint> Profile)> frameRows,
        string constraintMode)
    {
        var trajectory = simulation.BimodalTrajectory;
        if (trajectory is null || frameRows.Count == 0) return null;

        var leftAmplitude = trajectory.LeftAmplitudeCoefficients;
        var leftSigma = trajectory.LeftSigmaCoefficients;
        var rightAmplitude = trajectory.RightAmplitudeCoefficients;
        var rightSigma = trajectory.RightSigmaCoefficients;
        var separation = trajectory.SeparationCoefficients;
        if (leftAmplitude.Length == 0 || leftSigma.Length == 0 || rightAmplitude.Length == 0 || rightSigma.Length == 0 || separation.Length == 0)
        {
            return null;
        }

        double rmse = 0;
        double peakError = 0;
        double widthError = 0;
        double areaError = 0;
        double compromiseConsistency = 0;
        foreach (var row in frameRows)
        {
            var predictedParameters = _analysis.EvaluateSimulationBimodalParameters(trajectory, row.Tau);
            var predictedProfile = _analysis.BuildCenteredBimodalProfileFromParameters(predictedParameters, simulation.Width, simulation.ScanSizeNmX);
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
            MethodLabel = $"Exact Growth Model Equation ({ToGrowthModelModeLabel(constraintMode)})",
            DiscoveryMethod = "growth_model_exact",
            ActiveTerms = BuildSimulationActiveTerms(constraintMode),
            Coefficients = coefficientStats.ToDictionary(entry => entry.Key, entry => entry.Value.Mean, StringComparer.OrdinalIgnoreCase),
            CoefficientStatistics = coefficientStats,
            Rmse = rmse,
            PeakHeightError = peakError,
            WidthError = widthError,
            AreaError = areaError,
            CompromiseConsistency = compromiseConsistency,
            StabilityScore = 1.0,
            ComplexityPenalty = trajectory.Degree / 3.0,
            Confidence = 1.0,
            PseudotimeSensitivity = 0.0,
            BootstrapSupport = 1.0,
            MetaPriorScore = 1.0,
            Notes =
                "This candidate is the exact bimodal Gaussian trajectory used by the Growth Model solid curve. " +
                "Selecting it in Equation Discovery should reproduce the same simulated profile evolution frame-for-frame; other candidates are lower-order or higher-order approximations to this trajectory."
        };

        return new SimulationEquationCandidate
        {
            Display = display,
            Degree = trajectory.Degree,
            IsExactGrowthModel = true,
            LeftAmplitudeCoefficients = leftAmplitude,
            LeftSigmaCoefficients = leftSigma,
            RightAmplitudeCoefficients = rightAmplitude,
            RightSigmaCoefficients = rightSigma,
            SeparationCoefficients = separation
        };
    }

    private static double ComputeEquationRmseQualityFactor(double rmse)
    {
        if (rmse <= GoodEquationRmseThresholdNm) return 1.0;
        if (rmse >= FailEquationRmseThresholdNm) return 0.0;
        return Math.Clamp((FailEquationRmseThresholdNm - rmse) / (FailEquationRmseThresholdNm - GoodEquationRmseThresholdNm), 0.0, 1.0);
    }

    private static string DescribeEquationRmseQuality(double rmse)
    {
        if (rmse <= GoodEquationRmseThresholdNm) return "good";
        if (rmse <= FailEquationRmseThresholdNm) return "caution";
        return "fail";
    }

    private static bool IsStatisticallyInterpretableEquation(EquationCandidateResult candidate)
    {
        if (!double.IsFinite(candidate.Rmse) || !double.IsFinite(candidate.Confidence)) return false;
        if (candidate.Rmse > FailEquationRmseThresholdNm) return false;
        if (candidate.Confidence < 0.35) return false;
        return true;
    }

    private static string BuildEquationCandidateSignature(EquationCandidateResult candidate)
    {
        var coefficients = candidate.Coefficients
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Key}:{Math.Round(entry.Value, 8):G17}");
        return string.Join("|", coefficients);
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationEquationOverlaySeries(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
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
            var color = GetPseudoTimeColor(reference.Position01);
            series.Add(new PolylineSeries(NormalizeEquationCurvePoints(actual), color, 2.6, 0.98));
            if (predicted.Count > 1)
            {
                series.Add(new PolylineSeries(NormalizeEquationCurvePoints(predicted), color, 2.0, 0.74, Dashed: true));
            }
        }

        return series;
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationEquationProgressionSeries(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
        return Enumerable.Range(0, 7)
            .Select(index => index / 6.0)
            .Select(tau =>
            {
                var parameters = EvaluateSimulationEquationCandidate(candidate, tau);
                var profile = _analysis.BuildCenteredBimodalProfileFromParameters(parameters, simulation.Width, simulation.ScanSizeNmX);
                return new PolylineSeries(NormalizeEquationCurvePoints(profile), GetPseudoTimeColor(tau), 1.9, 0.98);
            })
            .Where(series => series.Points.Count > 1)
            .ToArray();
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationEquationDiagnosticsSeries(SurfaceSimulationResult simulation, SimulationEquationCandidate candidate)
    {
        var series = new List<PolylineSeries>
        {
            new(
                [new PlotPoint(-90.0, 0.0), new PlotPoint(90.0, 0.0)],
                "#9a8b78",
                1.2,
                0.55,
                Dashed: true)
        };

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
            var residual = BuildResidualSeries(actual, predicted);
            if (residual.Count < 2) continue;

            series.Add(new PolylineSeries(residual, GetPseudoTimeColor(reference.Position01), 2.0, 0.94));
        }

        return series;
    }

    private static string BuildSimulationEquationDiagnosticsSummary(SimulationEquationCandidate candidate)
    {
        var display = candidate.Display;
        return
            $"Selected equation: {display.MethodLabel}. Confidence {display.Confidence:P0}, RMSE {display.Rmse:F2} nm, " +
            $"mean peak-height error {display.PeakHeightError:F2} nm, mean width error {display.WidthError:F2} nm, " +
            $"stability {display.StabilityScore:F2}. Residual curves plot observed minus reconstructed height, so traces staying close to 0 nm indicate the best candidate.";
    }

    private static IReadOnlyList<PlotPoint> BuildResidualSeries(
        IReadOnlyList<EquationDiscoveryPoint> observed,
        IReadOnlyList<EquationDiscoveryPoint> reconstructed)
    {
        var observedPoints = NormalizeEquationCurvePoints(observed);
        var reconstructedPoints = NormalizeEquationCurvePoints(reconstructed);
        return BuildResidualSeries(observedPoints, reconstructedPoints);
    }

    private static IReadOnlyList<PlotPoint> BuildResidualSeries(
        IReadOnlyList<PlotPoint> observed,
        IReadOnlyList<PlotPoint> reconstructed)
    {
        var count = Math.Min(observed.Count, reconstructed.Count);
        if (count < 2) return Array.Empty<PlotPoint>();

        var residual = new PlotPoint[count];
        for (var index = 0; index < count; index++)
        {
            residual[index] = new PlotPoint(observed[index].X, observed[index].Y - reconstructed[index].Y);
        }

        return residual;
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

    private double[] FitConstraintAwareCurve(string constraintMode, string parameterKind, IReadOnlyList<double> taus, IReadOnlyList<double> values, int degree)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(constraintMode) ? "current" : constraintMode;
        var holdConstant =
            (string.Equals(normalizedMode, "constant_separation", StringComparison.OrdinalIgnoreCase) && string.Equals(parameterKind, "separation", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(normalizedMode, "constant_peak_width", StringComparison.OrdinalIgnoreCase) && string.Equals(parameterKind, "sigma", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(normalizedMode, "amplitude_only", StringComparison.OrdinalIgnoreCase);

        if (!holdConstant)
        {
            return _analysis.FitPolynomialCurve(taus, values, degree);
        }

        return values.Count == 0
            ? Array.Empty<double>()
            : [values.Average()];
    }

    private static IReadOnlyList<string> BuildSimulationActiveTerms(string constraintMode)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(constraintMode) ? "current" : constraintMode;
        return normalizedMode switch
        {
            "constant_separation" => new[] { "z_L(s,τ)", "z_R(s,τ)", "A_L(τ)", "σ_L(τ)", "A_R(τ)", "σ_R(τ)", "Δ = const", "s_L(τ)", "s_R(τ)", "s_c" },
            "constant_peak_width" => new[] { "z_L(s,τ)", "z_R(s,τ)", "A_L(τ)", "A_R(τ)", "σ_L = const", "σ_R = const", "Δ(τ)", "s_L(τ)", "s_R(τ)", "s_c" },
            "amplitude_only" => new[] { "z_L(s,τ)", "z_R(s,τ)", "A_L(τ)", "A_R(τ)", "σ_L = const", "σ_R = const", "Δ = const", "s_L", "s_R", "s_c" },
            _ => new[] { "z_L(s,τ)", "z_R(s,τ)", "A_L(τ)", "σ_L(τ)", "A_R(τ)", "σ_R(τ)", "s_L(τ)", "s_R(τ)", "Δ(τ)", "s_c" }
        };
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

    private static IReadOnlyList<EquationTermExplanation> BuildEquationTermExplanations(IEnumerable<EquationCandidateResult> candidates)
    {
        var discoveredSymbols = ExtractDiscoveredEquationSymbols(candidates);
        if (discoveredSymbols.Count == 0) return Array.Empty<EquationTermExplanation>();

        var knownExplanations = BuildKnownEquationTermExplanations()
            .GroupBy(explanation => explanation.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return discoveredSymbols
            .Select(symbol => knownExplanations.TryGetValue(symbol, out var explanation)
                ? explanation
                : new EquationTermExplanation
                {
                    Symbol = symbol,
                    Meaning = "Discovered model term",
                    Detail = "This symbol appears in the current discovered equation family for the guided profile stack."
                })
            .OrderBy(explanation => GetEquationTermPriority(explanation.Symbol))
            .ThenBy(explanation => explanation.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ExtractDiscoveredEquationSymbols(IEnumerable<EquationCandidateResult> candidates)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            foreach (var activeTerm in candidate.ActiveTerms)
            {
                var symbol = NormalizeDiscoveredEquationTerm(activeTerm);
                if (!string.IsNullOrWhiteSpace(symbol)) symbols.Add(symbol);
            }

            foreach (var coefficientKey in candidate.Coefficients.Keys)
            {
                foreach (var token in coefficientKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var symbol = NormalizeDiscoveredEquationTerm(token);
                    if (!string.IsNullOrWhiteSpace(symbol)) symbols.Add(symbol);
                }
            }
        }

        return symbols;
    }

    private static string NormalizeDiscoveredEquationTerm(string term)
    {
        var trimmed = (term ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

        return trimmed switch
        {
            "tau" => "τ",
            "A1" => "A_L(τ), A_R(τ)",
            "A2" => "A_L(τ), A_R(τ)",
            "sigma1" => "σ_L(τ), σ_R(τ)",
            "sigma2" => "σ_L(τ), σ_R(τ)",
            "D" => "Δ(τ)",
            "mu1" => "s_L(τ), s_R(τ)",
            "mu2" => "s_L(τ), s_R(τ)",
            "z" => "z(s, τ)",
            "z_L(s,τ)" => "z_L(s, τ), z_R(s, τ)",
            "z_R(s,τ)" => "z_L(s, τ), z_R(s, τ)",
            "z_L(s, τ)" => "z_L(s, τ), z_R(s, τ)",
            "z_R(s, τ)" => "z_L(s, τ), z_R(s, τ)",
            "A_L(τ)" => "A_L(τ), A_R(τ)",
            "A_R(τ)" => "A_L(τ), A_R(τ)",
            "σ_L(τ)" => "σ_L(τ), σ_R(τ)",
            "σ_R(τ)" => "σ_L(τ), σ_R(τ)",
            "s_L(τ)" => "s_L(τ), s_R(τ)",
            "s_R(τ)" => "s_L(τ), s_R(τ)",
            _ => trimmed
        };
    }

    private static int GetEquationTermPriority(string symbol)
    {
        return symbol switch
        {
            "z(s, τ)" => 0,
            "τ" => 1,
            "A_L(τ), A_R(τ)" => 2,
            "σ_L(τ), σ_R(τ)" => 3,
            "s_L(τ), s_R(τ)" => 4,
            "Δ(τ)" => 5,
            "z_L(s, τ), z_R(s, τ)" => 6,
            "s_c" => 7,
            "s" => 8,
            _ => 99
        };
    }

    private static IReadOnlyList<EquationTermExplanation> BuildKnownEquationTermExplanations() =>
    [
        new EquationTermExplanation
        {
            Symbol = "z(s, τ)",
            Meaning = "Height profile over pseudo-time",
            Detail = "The discovered law evolves AFM height z along the aligned centreline coordinate s as latent growth progression τ increases."
        },
        new EquationTermExplanation
        {
            Symbol = "τ",
            Meaning = "Pseudo-time / progression variable",
            Detail = "Ordered stage progression anchor, not real clock time."
        },
        new EquationTermExplanation
        {
            Symbol = "1",
            Meaning = "Constant term",
            Detail = "A baseline offset term in the discovered equation."
        },
        new EquationTermExplanation
        {
            Symbol = "z",
            Meaning = "Profile height state",
            Detail = "The current local height value used directly in the discovered field equation."
        },
        new EquationTermExplanation
        {
            Symbol = "z^2",
            Meaning = "Quadratic height term",
            Detail = "Captures nonlinear self-interaction of the local height field."
        },
        new EquationTermExplanation
        {
            Symbol = "z^3",
            Meaning = "Cubic height term",
            Detail = "A higher-order nonlinear height contribution used only when the discovered family needs extra shape flexibility."
        },
        new EquationTermExplanation
        {
            Symbol = "dz/ds",
            Meaning = "Local slope",
            Detail = "How fast the profile height changes along the aligned centreline."
        },
        new EquationTermExplanation
        {
            Symbol = "d2z/ds2",
            Meaning = "Curvature",
            Detail = "How sharply the profile bends; positive and negative values indicate different local shape changes."
        },
        new EquationTermExplanation
        {
            Symbol = "z_L(s, τ), z_R(s, τ)",
            Meaning = "Left and right Gaussian ridge profiles",
            Detail = "The reconstructed profile is written as a sum of two Gaussian contributions so the tramline shape stays explicitly bimodal."
        },
        new EquationTermExplanation
        {
            Symbol = "A_L(τ), A_R(τ)",
            Meaning = "Left and right peak heights",
            Detail = "These are the state variables for left and right tramline amplitude, and the discovered ODE system governs how they change with pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "σ_L(τ), σ_R(τ)",
            Meaning = "Left and right peak widths",
            Detail = "These determine how broad or narrow each Gaussian rim is at a given pseudo-time and are evolved directly by the fitted feature dynamics."
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
            Detail = "Controls how far apart the two bimodal rims are at each pseudo-time, so tramline splitting is visible as Delta grows."
        },
        new EquationTermExplanation
        {
            Symbol = "A_mean",
            Meaning = "Mean peak amplitude",
            Detail = "The average of the left and right peak heights used as a reduced feature in the feature-evolution ODE."
        },
        new EquationTermExplanation
        {
            Symbol = "sigma_mean",
            Meaning = "Mean peak width",
            Detail = "The average of the left and right Gaussian widths used as a compact feature in the discovered dynamics."
        },
        new EquationTermExplanation
        {
            Symbol = "ratio_minus_1",
            Meaning = "Amplitude asymmetry",
            Detail = "Measures how different the left and right peak heights are relative to each other."
        },
        new EquationTermExplanation
        {
            Symbol = "D_over_sigma",
            Meaning = "Separation-to-width ratio",
            Detail = "A compact bimodality measure comparing tramline separation with the average peak width."
        },
        new EquationTermExplanation
        {
            Symbol = "tau^2",
            Meaning = "Quadratic pseudo-time term",
            Detail = "Lets the discovered dynamics curve over progression rather than changing only linearly with pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "z*tau",
            Meaning = "Height-time interaction",
            Detail = "Captures how the effect of the local height changes across pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "A_mean*tau",
            Meaning = "Amplitude-time interaction",
            Detail = "Lets the average rim amplitude influence the discovered dynamics differently at different stages of progression."
        },
        new EquationTermExplanation
        {
            Symbol = "D*tau",
            Meaning = "Separation-time interaction",
            Detail = "Lets the effect of tramline separation vary across pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "z*(dz/ds)",
            Meaning = "Height-slope interaction",
            Detail = "A nonlinear coupling between local height and local slope."
        },
        new EquationTermExplanation
        {
            Symbol = "z*(d2z/ds2)",
            Meaning = "Height-curvature interaction",
            Detail = "A nonlinear term coupling height to local curvature."
        },
        new EquationTermExplanation
        {
            Symbol = "(dz/ds)^2",
            Meaning = "Squared slope term",
            Detail = "Captures symmetric steepness effects regardless of slope sign."
        },
        new EquationTermExplanation
        {
            Symbol = "z^2*(dz/ds)",
            Meaning = "Quadratic height-slope interaction",
            Detail = "A higher-order nonlinear interaction between height and slope."
        },
        new EquationTermExplanation
        {
            Symbol = "z^2*(d2z/ds2)",
            Meaning = "Quadratic height-curvature interaction",
            Detail = "A higher-order nonlinear interaction between height and curvature."
        },
        new EquationTermExplanation
        {
            Symbol = "(d2z/ds2)*tau",
            Meaning = "Curvature-time interaction",
            Detail = "Lets curvature contribute differently across pseudo-time."
        },
        new EquationTermExplanation
        {
            Symbol = "d3z/ds3",
            Meaning = "Third spatial derivative",
            Detail = "Captures asymmetric shape changes and sharper transitions in the guided profile."
        },
        new EquationTermExplanation
        {
            Symbol = "d4z/ds4",
            Meaning = "Fourth spatial derivative",
            Detail = "Captures higher-order smoothing or sharpening behaviour in the discovered field equation."
        },
        new EquationTermExplanation
        {
            Symbol = "s_c",
            Meaning = "Centred reference position",
            Detail = "The fixed aligned centre of the guided profile. The left and right peaks move around this anchor."
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
        var peakSeparation = ComputeProfilePeakSeparationNm(profile);
        if (peakSeparation > 1e-9) return peakSeparation;
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

    private static double ComputeProfilePeakSeparationNm(IReadOnlyList<PlotPoint> profile)
    {
        if (profile.Count < 5) return 0;
        var mid = profile.Count / 2;
        var leftIndex = profile
            .Take(Math.Max(2, mid))
            .Select((point, index) => (point.Y, index))
            .OrderByDescending(item => item.Y)
            .First().index;
        var rightIndex = mid + profile
            .Skip(mid)
            .Select((point, index) => (point.Y, index))
            .OrderByDescending(item => item.Y)
            .First().index;
        if (rightIndex <= leftIndex + 1) return 0;

        var leftPeak = profile[leftIndex].Y;
        var rightPeak = profile[rightIndex].Y;
        var globalPeak = Math.Max(leftPeak, rightPeak);
        if (!(globalPeak > 1e-9)) return 0;

        var valley = profile.Skip(leftIndex).Take(rightIndex - leftIndex + 1).Min(point => point.Y);
        var valleyDepth = ((leftPeak + rightPeak) * 0.5) - valley;
        if (valleyDepth < globalPeak * 0.05) return 0;

        return Math.Max(0, Math.Abs(profile[rightIndex].X - profile[leftIndex].X));
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
        if (!EnsureDistinctSimulationReferences("Simulation playback")) return;

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
        if (!EnsureDistinctSimulationReferences("Polynomial evolution")) return;

        InvalidateSimulationCache();
        SimulationProgress = 0;
        RefreshSimulationSeries();
        if (_surfaceSimulationCache is null)
        {
            RaiseUserAlert(
                "Polynomial Evolution Unavailable",
                "The selected references did not generate a usable polynomial evolution. Confirm that both files have guided extractions and distinct ordered sequence anchors.");
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
            CurrentProfileYAxisLabel = "Raw height [nm]";
            EvolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
            EvolutionYAxisLabel = "Height above local baseline [nm]";
            SimulationXAxisLabel = "Centered x [nm]";
            SimulationYAxisLabel = "Height above local baseline [nm]";
            SimulationReferenceSummaryText = "Ordered references: -";
            SimulationSurfaceMetaText = "Surface frame: -";
            SimulationSurfaceXAxisLabel = "Centered x [nm]";
            SimulationSurfaceYAxisLabel = "Aligned y [nm]";
            SelectedStageHintText = "Stage is auto-classified on load, but you can change it manually. Choose 'none' to leave stage assignment out of the way.";
            return;
        }

        SelectedFileNameText = SelectedFile.Name;
        SelectedChannelDisplayText = $"Channel: {SelectedFile.ChannelDisplay}";
        SelectedDisplayMinText = $"Display Min: {SelectedFile.DisplayMin:F2}";
        SelectedDisplayMaxText = $"Display Max: {SelectedFile.DisplayMax:F2}";
        SelectedDisplayReferenceText = $"Display Reference: {SelectedFile.DisplayReferenceNm:F2} {SelectedFile.Unit}";
        SelectedEstimatedNoiseText = $"Estimated Noise Sigma: {SelectedFile.EstimatedNoiseSigma:F3} {SelectedFile.Unit}";
        CurrentProfileXAxisLabel = $"x [{SelectedFile.Unit}]";
            CurrentProfileYAxisLabel = $"Raw height [{SelectedFile.Unit}]";
        EvolutionXAxisLabel = "Relative lateral position [% of extracted profile]";
        EvolutionYAxisLabel = $"Height above local baseline [{SelectedFile.Unit}]";
        SimulationXAxisLabel = $"Centered corridor offset [{SelectedFile.Unit}]";
        SimulationYAxisLabel = $"Height above local baseline [{SelectedFile.Unit}]";
        SimulationSurfaceXAxisLabel = $"Centered corridor offset [{SelectedFile.Unit}]";
        SimulationSurfaceYAxisLabel = $"Guide distance [{SelectedFile.Unit}]";
        SelectedStageHintText = string.Equals(SelectedFile.Stage, "none", StringComparison.OrdinalIgnoreCase)
            ? "Stage is set to 'none', so the app will rely on sequencing and profile progression instead of a manual stage label."
            : $"Stage is currently '{SelectedFile.Stage}'. You can override the auto-assigned stage, or choose 'none' to defer to sequencing.";

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
        if (_isApplyingAutomaticSequenceOrdering && e.PropertyName == nameof(PiecrustFileState.SequenceOrder)) return;
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

    partial void OnSelectedEquationCandidateChanged(EquationCandidateResult? value)
    {
        if (value is null || _simulationEquationCandidates.Count == 0) return;
        var simulation = GetOrBuildSimulationCache();
        if (simulation is null) return;
        ApplySelectedSimulationEquationCandidate(simulation);
    }

    private void RefreshSimulationSeries()
    {
        if (SimulationStartFile is null || SimulationEndFile is null)
        {
            SimulationSurfaceBitmap = null;
            SimulationSeries = Array.Empty<PolylineSeries>();
            SimulationPlotFixedXMin = double.NaN;
            SimulationPlotFixedXMax = double.NaN;
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
            SimulationPlotFixedXMin = double.NaN;
            SimulationPlotFixedXMax = double.NaN;
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
        SimulationXAxisLabel = simulation.UsesGuidedAlignment ? $"Centered corridor offset [{simulation.Unit}]" : $"Centered x [{simulation.Unit}]";
        SimulationSurfaceXAxisLabel = simulation.UsesGuidedAlignment ? $"Centered corridor offset [{simulation.Unit}]" : $"Centered x [{simulation.Unit}]";
        SimulationSurfaceYAxisLabel = simulation.UsesGuidedAlignment ? $"Guide distance [{simulation.Unit}]" : $"Aligned y [{simulation.Unit}]";
        SimulationSeries = BuildSimulationPlotSeries(simulation, currentFrame, SimulationProgress);
        SimulationPlotFixedXMin = -simulation.ScanSizeNmX / 2.0;
        SimulationPlotFixedXMax = simulation.ScanSizeNmX / 2.0;
        SimulationPlotFixedYMin = 0;
        SimulationPlotFixedYMax = GetSimulationPlotYMax(simulation);
        SimulationPlotLegendText = $"Dotted = centered evolving cross-section | Solid = {ToGrowthModelModeLabel(simulation.ConstraintMode)} bimodal Gaussian fit";
        SimulationReferenceSummaryText = BuildSimulationReferenceSummary(simulation);
        var alignmentText = simulation.UsesGuidedAlignment
            ? "The simulation uses the guided corridor region, widened to corridor + 20%, then keeps each cross-section centered so the morphology grows in place instead of drifting laterally."
            : "Guided alignment was unavailable for one or more references, so full-image surfaces were used.";
        SimulationSurfaceMetaText = $"Simulation span {(int)Math.Round(SimulationProgress * (simulation.Frames.Count - 1)) + 1}/{simulation.Frames.Count}  |  centered cross-section: {-simulation.ScanSizeNmX / 2.0:F1} to {simulation.ScanSizeNmX / 2.0:F1} {simulation.Unit}  |  guide distance: 0-{simulation.ScanSizeNmY:F1} {simulation.Unit}  |  {alignmentText}";
        SimulationStatusText = simulation.UsesSupervisedLearning
            ? $"Polynomial surface evolution (degree {simulation.PolynomialDegree}) across {simulation.References.Count} ordered reference point(s), guided by a learned profile-growth model trained on {simulation.SupervisedExampleCount} stored example(s). Active bimodal mode: {ToGrowthModelModeLabel(simulation.ConstraintMode)}. The dotted curve is the centered evolving simulated cross-section, and the solid curve is the matched constrained bimodal Gaussian growth fit."
            : $"Polynomial surface evolution (degree {simulation.PolynomialDegree}) across {simulation.References.Count} ordered reference point(s). Active bimodal mode: {ToGrowthModelModeLabel(simulation.ConstraintMode)}. The dotted curve is the centered evolving simulated cross-section, and the solid curve is the matched constrained bimodal Gaussian growth fit.";
        SupervisedModelStatusText = _supervisedGrowthLearning.DescribeModel(_supervisedGrowthModel);
    }

    private SurfaceSimulationResult? GetOrBuildSimulationCache()
    {
        if (_surfaceSimulationCache is not null) return _surfaceSimulationCache;
        if (SimulationStartFile is null || SimulationEndFile is null || ReferenceEquals(SimulationStartFile, SimulationEndFile)) return null;
        _surfaceSimulationCache = _analysis.BuildSurfaceSimulation(Files, SimulationStartFile, SimulationEndFile, _supervisedGrowthModel, NormalizeGrowthModelMode(SelectedGrowthModelMode));
        return _surfaceSimulationCache;
    }

    private void InvalidateSimulationCache()
    {
        _surfaceSimulationCache = null;
        SimulationSurfaceBitmap = null;
    }

    private IReadOnlyList<PolylineSeries> BuildSimulationPlotSeries(SurfaceSimulationResult simulation, double[] currentFrame, double progress)
    {
        var rawProfile = _analysis.BuildSurfaceCrossSection(currentFrame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        var fittedProfile = _analysis.BuildBimodalPolynomialSimulationProfile(simulation, progress);
        if (fittedProfile.Count == 0)
        {
            fittedProfile = _analysis.BuildCenteredBimodalSimulationProfile(currentFrame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        }
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
            .SelectMany((frame, index) => GetSimulationPlotProfiles(simulation, frame, simulation.FrameProgresses[index]))
            .SelectMany(profile => profile)
            .Where(point => double.IsFinite(point.Y))
            .Select(point => point.Y)
            .DefaultIfEmpty(1)
            .Max();

        return Math.Max(1, maxY * 1.08);
    }

    private IEnumerable<IReadOnlyList<PlotPoint>> GetSimulationPlotProfiles(SurfaceSimulationResult simulation, double[] frame, double progress)
    {
        var rawProfile = _analysis.BuildSurfaceCrossSection(frame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        if (rawProfile.Count > 0)
        {
            yield return rawProfile;
        }

        var fittedProfile = _analysis.BuildBimodalPolynomialSimulationProfile(simulation, progress);
        if (fittedProfile.Count == 0)
        {
            fittedProfile = _analysis.BuildCenteredBimodalSimulationProfile(frame, simulation.Width, simulation.Height, simulation.ScanSizeNmX);
        }
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
        return $"Ordered reference stages used in the fit: {string.Join("  ->  ", parts)}  |  Mode: {ToGrowthModelModeLabel(simulation.ConstraintMode)}";
    }

    private static string ToStageLabel(string stage) => stage switch
    {
        "none" => "None",
        "early" => "Early",
        "middle" => "Middle",
        "late" => "Late",
        _ => string.IsNullOrWhiteSpace(stage) ? "Unassigned" : char.ToUpperInvariant(stage[0]) + stage[1..]
    };

    private static string NormalizeGrowthModelMode(string mode) => mode switch
    {
        "Current Free Model" => "current",
        "Constant Separation" => "constant_separation",
        "Constant Peak Width" => "constant_peak_width",
        "Amplitude Only" => "amplitude_only",
        "constant_separation" => "constant_separation",
        "constant_peak_width" => "constant_peak_width",
        "amplitude_only" => "amplitude_only",
        _ => "current"
    };

    private static string ToGrowthModelModeLabel(string mode) => NormalizeGrowthModelMode(mode) switch
    {
        "constant_separation" => "Constant Separation",
        "constant_peak_width" => "Constant Peak Width",
        "amplitude_only" => "Amplitude Only",
        _ => "Current Free Model"
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
            SelectedSequencingMode = SelectedSequencingMode,
            SelectedGrowthModelMode = NormalizeGrowthModelMode(SelectedGrowthModelMode),
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
