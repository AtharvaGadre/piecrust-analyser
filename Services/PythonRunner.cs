using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PiecrustAnalyser.CSharp.Services;

public static class PythonRunner
{
    public static async Task<string> RunAnalysisAsync(string tiffPath, string outputDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tiffPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);

        Directory.CreateDirectory(outputDir);
        var scriptPath = ResolveStage1ScriptPath();
        var summaryPath = Path.Combine(outputDir, "summary_report.json");
        var stderr = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--tiff_path");
        startInfo.ArgumentList.Add(tiffPath);
        startInfo.ArgumentList.Add("--output_dir");
        startInfo.ArgumentList.Add(outputDir);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Console.WriteLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
                Console.Error.WriteLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Python analysis process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Python analysis failed with exit code {process.ExitCode}.{Environment.NewLine}{stderr.ToString().Trim()}");
        }

        if (!File.Exists(summaryPath))
        {
            throw new FileNotFoundException("Python analysis completed but did not produce summary_report.json.", summaryPath);
        }

        return summaryPath;
    }

    private static string ResolvePythonExecutable()
    {
        var env = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
    }

    private static string ResolveStage1ScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Python", "stage1_surface_extraction.py"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "stage1_surface_extraction.py")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "stage1_surface_extraction.py"))
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate stage1_surface_extraction.py for Python analysis.");
    }
}
