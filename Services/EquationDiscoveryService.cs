using System.Diagnostics;
using System.Text.Json;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class EquationDiscoveryService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private string ArchivePath
    {
        get
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PieCrustAnalyser");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "equation-discovery-archive.json");
        }
    }

    public async Task<EquationDiscoveryResult?> DiscoverAsync(EquationDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Files.Count < 2) return null;

        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("Equation discovery script was not found.", scriptPath);

        var workingDirectory = Path.Combine(Path.GetTempPath(), "piecrust-equation-discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var inputPath = Path.Combine(workingDirectory, "input.json");
        var outputPath = Path.Combine(workingDirectory, "output.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request, _jsonOptions), cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        foreach (var candidate in GetPythonCommandCandidates())
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate.FileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };
                startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
                startInfo.Environment["MPLCONFIGDIR"] = workingDirectory;
                foreach (var argument in candidate.ArgumentsPrefix) startInfo.ArgumentList.Add(argument);
                startInfo.ArgumentList.Add(scriptPath);
                startInfo.ArgumentList.Add(inputPath);
                startInfo.ArgumentList.Add(outputPath);
                startInfo.ArgumentList.Add(ArchivePath);

                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {candidate.FileName}.");
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    lastError = new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                    continue;
                }

                if (!File.Exists(outputPath))
                {
                    lastError = new InvalidOperationException("Equation discovery finished without producing an output file.");
                    continue;
                }

                var json = await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    lastError = new InvalidOperationException("Equation discovery returned an empty result.");
                    continue;
                }

                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var details = errorElement.GetString() ?? "Unknown equation discovery error.";
                    if (document.RootElement.TryGetProperty("traceback", out var tracebackElement))
                    {
                        details = $"{details}{Environment.NewLine}{tracebackElement.GetString()}";
                    }
                    throw new InvalidOperationException(details);
                }

                var result = JsonSerializer.Deserialize<EquationDiscoveryResult>(json, _jsonOptions);
                if (result is null) throw new InvalidOperationException("Equation discovery output could not be parsed.");
                result.RawJson = json;
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Equation discovery could not start because no Python interpreter was available.");
    }

    private static string ResolveScriptPath()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Python", "equation_discovery.py"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Python", "equation_discovery.py")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Python", "equation_discovery.py"))
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("Equation discovery script was not found in the application output or project Python folder.");
    }

    private static IReadOnlyList<(string FileName, string[] ArgumentsPrefix)> GetPythonCommandCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                ("py", new[] { "-3" }),
                ("python", Array.Empty<string>())
            };
        }

        return new[]
        {
            ("python3", Array.Empty<string>()),
            ("python", Array.Empty<string>())
        };
    }
}
