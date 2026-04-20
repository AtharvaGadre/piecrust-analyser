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
    private MainWindowViewModel? _subscribedVm;

    public MainWindow()
    {
        InitializeComponent();
        HeightMapCanvas.PixelSelected += OnPixelSelected;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        DataContextChanged += OnDataContextChanged;
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
            Vm.ReportRecoverableError("Load Failed", $"Load failed: {ex.Message}");
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
    private void OnSimulateSelectedEquationClick(object? sender, RoutedEventArgs e) => Vm.SimulateSelectedEquationCandidate();
    private void OnToggleEquationPlaybackClick(object? sender, RoutedEventArgs e) => Vm.ToggleEquationPlayback();
    private void OnResetEquationPlaybackClick(object? sender, RoutedEventArgs e) => Vm.ResetEquationPlayback();

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
            Vm.ReportRecoverableError("Drop Failed", "No files were detected in the drop payload.");
            return;
        }

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
        {
            Vm.ReportRecoverableError("Drop Failed", "Dropped items were not local files.");
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
            Vm.ReportRecoverableError("Nothing To Export", "There is no data to export for this section yet.");
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.UserAlertRequested -= OnUserAlertRequested;
        }

        _subscribedVm = DataContext as MainWindowViewModel;
        if (_subscribedVm is not null)
        {
            _subscribedVm.UserAlertRequested += OnUserAlertRequested;
        }
    }

    private async void OnUserAlertRequested(object? sender, UserAlertRequestedEventArgs e)
    {
        var dialog = new Window
        {
            Title = e.Title,
            Width = 480,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brush.Parse("#161008"),
            Foreground = Avalonia.Media.Brush.Parse("#ead9bf"),
            Content = new Border
            {
                Padding = new Avalonia.Thickness(18),
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = e.Title,
                            FontSize = 18,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Foreground = Avalonia.Media.Brush.Parse("#f0c978"),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = e.Message,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            MinWidth = 90
                        }
                    }
                }
            }
        };

        if (dialog.Content is Border { Child: StackPanel { Children.Count: > 0 } panel } &&
            panel.Children[^1] is Button button)
        {
            button.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(this);
    }
}
