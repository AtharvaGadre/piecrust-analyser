using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PiecrustAnalyser.CSharp.ViewModels;
using PiecrustAnalyser.CSharp.Views;

namespace PiecrustAnalyser.CSharp;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogUnhandledException(args.ExceptionObject as Exception);
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            LogUnhandledException(args.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                lifetime.MainWindow?.DataContext is MainWindowViewModel vm)
            {
                vm.StatusText = $"Unexpected error: {args.Exception.Message}";
            }
            args.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogUnhandledException(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "piecrust-analyser-csharp-error.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // Ignore logging failures and keep the UI alive.
        }
    }
}
