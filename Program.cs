using Avalonia;
using PiecrustAnalyser.CSharp.Services;
using System;

namespace PiecrustAnalyser.CSharp;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--self-test-load")
        {
            RunSelfTest(args[1]).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static async Task RunSelfTest(string path)
    {
        var loader = new FileLoadingService();
        var analysis = new PiecrustAnalysisService();
        var loaded = await loader.LoadAsync(path, 500);
        if (loaded is null)
        {
            Console.WriteLine("LOAD: null");
            return;
        }

        Console.WriteLine($"LOAD: {loaded.Name} {loaded.Width}x{loaded.Height} {loaded.Format}");
        Console.WriteLine($"RANGE: {loaded.Data.Min():F3} -> {loaded.Data.Max():F3}");
        Console.WriteLine($"STAGE: {analysis.ClassifyStage(loaded.Data)}");
    }
}
