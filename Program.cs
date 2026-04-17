using Avalonia;
using PiecrustAnalyser.CSharp.Services;
using System;
using System.IO;
using System.Linq;

namespace PiecrustAnalyser.CSharp;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--headless-self-test")
        {
            var explicitPath = args.Length >= 2 ? args[1] : null;
            RunHeadlessSelfTest(explicitPath).GetAwaiter().GetResult();
            return;
        }

        if (args.Length >= 2 && args[0] == "--self-test-load")
        {
            RunSelfTest(args[1]).GetAwaiter().GetResult();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (InvalidOperationException ex) when (IsHeadlessRenderTimerFailure(ex))
        {
            Console.Error.WriteLine($"GUI startup is unavailable in this session ({ex.Message}).");
            Console.Error.WriteLine("Falling back to headless self-test mode.");
            RunHeadlessSelfTest(null).GetAwaiter().GetResult();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
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

    private static bool IsHeadlessRenderTimerFailure(Exception ex)
    {
        if (ex is null) return false;
        if (ex.Message.Contains("RenderTimer", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Avalonia.Native", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException is not null && IsHeadlessRenderTimerFailure(ex.InnerException);
    }

    private static async Task RunHeadlessSelfTest(string? explicitPath)
    {
        var candidates = new[]
        {
            explicitPath,
            Path.Combine(Environment.CurrentDirectory, "..", "pipeline_test", "frame_0000.tiff"),
            Path.Combine(Environment.CurrentDirectory, "..", "pipeline_test", "frame_0001.tiff"),
            Path.Combine(Environment.CurrentDirectory, "pipeline_test", "frame_0000.tiff"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "pipeline_test", "frame_0000.tiff"),
        }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
        {
            Console.WriteLine("HEADLESS SELF-TEST: no sample image was found; startup fallback completed.");
            return;
        }

        Console.WriteLine($"HEADLESS SELF-TEST: loading {existing}");
        await RunSelfTest(existing);
    }
}
