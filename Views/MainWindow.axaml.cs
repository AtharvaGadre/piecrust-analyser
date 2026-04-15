using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PiecrustAnalyser.CSharp.Controls;
using PiecrustAnalyser.CSharp.Services;
using PiecrustAnalyser.CSharp.ViewModels;

namespace PiecrustAnalyser.CSharp.Views;

public partial class MainWindow : Window
{
    private readonly NativeFileDialogService _nativeFileDialog = new();

    public MainWindow()
    {
        InitializeComponent();
        HeightMapCanvas.PixelSelected += OnPixelSelected;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private async void OnLoadFilesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var selectedPaths = await _nativeFileDialog.PickFilesAsync();
                if (selectedPaths.Length == 0)
                {
                    Vm.StatusText = "No files were selected. You can also drag files into the window.";
                    return;
                }
                await Vm.LoadFilesAsync(selectedPaths);
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select TIFF/SPM files",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Microscopy files")
                    {
                        Patterns = ["*.spm", "*.dat", "*.nid", "*.tif", "*.tiff", "*.png", "*.jpg", "*.jpeg"]
                    }
                ]
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
            await Vm.LoadFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Load failed: {ex.Message}";
        }
    }

    private void OnAutoNumberFilesClick(object? sender, RoutedEventArgs e) => Vm.AutoNumberFiles();
    private void OnRemoveSelectedClick(object? sender, RoutedEventArgs e) => Vm.RemoveSelectedFile();
    private void OnMarkProfileLineClick(object? sender, RoutedEventArgs e) => Vm.BeginProfileLineSelection();
    private void OnClearProfileLineClick(object? sender, RoutedEventArgs e) => Vm.ClearProfileLine();
    private void OnStartGuideClick(object? sender, RoutedEventArgs e) => Vm.StartGuideLine();
    private void OnFinishGuideClick(object? sender, RoutedEventArgs e) => Vm.FinishGuideLine();
    private void OnUndoGuidePointClick(object? sender, RoutedEventArgs e) => Vm.UndoGuidePoint();
    private void OnClearGuideClick(object? sender, RoutedEventArgs e) => Vm.ClearGuideLine();
    private void OnRunGuidedExtractionClick(object? sender, RoutedEventArgs e) => Vm.RunGuidedExtraction();
    private void OnSelectionFieldChanged(object? sender, RoutedEventArgs e) => Vm.RefreshAfterSelectionEdit();
    private void OnSetAutoDisplayFixedClick(object? sender, RoutedEventArgs e) => Vm.SetAutoDisplayAsFixed();
    private void OnRunPolynomialEvolutionClick(object? sender, RoutedEventArgs e) => Vm.RunPolynomialEvolution();
    private void OnToggleSimulationClick(object? sender, RoutedEventArgs e) => Vm.ToggleSimulationPlayback();
    private void OnResetSimulationClick(object? sender, RoutedEventArgs e) => Vm.ResetSimulationPlayback();
    private async void OnDiscoverGrowthEquationsClick(object? sender, RoutedEventArgs e) => await Vm.DiscoverGrowthEquationsAsync();

    private void OnPixelSelected(object? sender, PixelSelectedEventArgs e) => Vm.HandleCanvasClick(e.Point);

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        await SaveCsvAsync(Vm.BuildCurrentResultsCsv(), "Save guided results CSV", "piecrust-guided-results.csv");
    }

    private async void OnExportLineProfileCsvClick(object? sender, RoutedEventArgs e) =>
        await SaveCsvAsync(Vm.BuildCurrentLineProfileCsv(), "Save line profile CSV", "piecrust-line-profile.csv");

    private async void OnExportBoxPlotsCsvClick(object? sender, RoutedEventArgs e) =>
        await SaveCsvAsync(Vm.BuildStageBoxPlotsCsv(), "Save stage box plot CSV", "piecrust-stage-box-plots.csv");

    private async void OnExportGrowthModelCsvClick(object? sender, RoutedEventArgs e) =>
        await SaveCsvAsync(Vm.BuildGrowthModelCsv(), "Save growth model CSV", "piecrust-growth-model.csv");

    private async void OnExportGrowthQuantCsvClick(object? sender, RoutedEventArgs e) =>
        await SaveCsvAsync(Vm.BuildGrowthQuantificationCsv(), "Save growth quantification CSV", "piecrust-growth-quant.csv");

    private async void OnExportEquationDiscoveryCsvClick(object? sender, RoutedEventArgs e) =>
        await SaveCsvAsync(Vm.BuildEquationDiscoveryCsv(), "Save equation discovery CSV", "piecrust-equation-discovery.csv");

    private async void OnExportEquationDiscoveryJsonClick(object? sender, RoutedEventArgs e) =>
        await SaveJsonAsync(Vm.BuildEquationDiscoveryJson(), "Save equation discovery JSON", "piecrust-equation-discovery.json");

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0)
        {
            Vm.StatusText = "No files were detected in the drop payload.";
            return;
        }

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
        {
            Vm.StatusText = "Dropped items were not local files.";
            return;
        }

        await Vm.LoadFilesAsync(paths);
    }

    private async Task SaveCsvAsync(string csv, string title, string suggestedFileName)
    {
        await SaveTextAsync(csv, title, suggestedFileName, "CSV", "*.csv");
    }

    private async Task SaveJsonAsync(string json, string title, string suggestedFileName)
    {
        await SaveTextAsync(json, title, suggestedFileName, "JSON", "*.json");
    }

    private async Task SaveTextAsync(string content, string title, string suggestedFileName, string label, string pattern)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            Vm.StatusText = "There is no data to export for this section yet.";
            return;
        }

        string? path;
        if (OperatingSystem.IsMacOS())
        {
            path = await _nativeFileDialog.PickSavePathAsync(title, suggestedFileName);
        }
        else
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                FileTypeChoices =
                [
                    new FilePickerFileType(label)
                    {
                        Patterns = [pattern]
                    }
                ]
            });
            path = file?.TryGetLocalPath();
        }

        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, content);
        Vm.StatusText = $"Saved {label} to {Path.GetFileName(path)}";
    }
}
