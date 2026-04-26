using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class EquationDiscoveryStageValidationException : InvalidOperationException
{
    public EquationDiscoveryStageValidationException(string message, EquationDiscoveryStageValidation? stageValidation = null)
        : base(message)
    {
        StageValidation = stageValidation;
    }

    public EquationDiscoveryStageValidation? StageValidation { get; }
}

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
        var launchErrors = new List<string>();
        foreach (var candidate in GetPythonCommandCandidates())
        {
            try
            {
                if (!Directory.Exists(workingDirectory))
                {
                    Directory.CreateDirectory(workingDirectory);
                }

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
                    if (LooksLikeMissingUnixPythonAlias(stderr, stdout))
                    {
                        launchErrors.Add($"{candidate.FileName} {string.Join(' ', candidate.ArgumentsPrefix)}".Trim());
                        continue;
                    }
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
                var stageValidation = ParseStageValidation(document.RootElement);
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var details = errorElement.GetString() ?? "Unknown equation discovery error.";
                    if (document.RootElement.TryGetProperty("traceback", out var tracebackElement))
                    {
                        details = $"{details}{Environment.NewLine}{tracebackElement.GetString()}";
                    }

                    if (stageValidation is { ValidatorAvailable: true, ConfidenceScore: < 0.5 })
                    {
                        throw new EquationDiscoveryStageValidationException(
                            BuildLowConfidenceMessage(stageValidation, details),
                            stageValidation);
                    }

                    throw new InvalidOperationException(details);
                }

                if (stageValidation is { ValidatorAvailable: true, ConfidenceScore: < 0.5 })
                {
                    throw new EquationDiscoveryStageValidationException(
                        BuildLowConfidenceMessage(stageValidation),
                        stageValidation);
                }

                if (stageValidation is { ValidatorAvailable: true, ConfidenceScore: < 0.8 })
                {
                    var proceed = await ConfirmMediumConfidenceAsync(stageValidation, cancellationToken).ConfigureAwait(false);
                    if (!proceed)
                    {
                        throw new OperationCanceledException(
                            $"Equation discovery was cancelled because stage-ordering confidence is only {stageValidation.ConfidenceScore:P0}.",
                            cancellationToken);
                    }
                }

                var result = JsonSerializer.Deserialize<EquationDiscoveryResult>(json, _jsonOptions);
                if (result is null) throw new InvalidOperationException("Equation discovery output could not be parsed.");
                if (result.EquationFamily.Count == 0)
                {
                    throw new InvalidOperationException(
                        BuildResultConsistencyMessage(
                            "Equation discovery returned no candidate equations.",
                            stdout,
                            stderr));
                }
                if (result.ObservedProfiles.Count == 0 || result.ProgressionProfiles.Count == 0)
                {
                    throw new InvalidOperationException(
                        BuildResultConsistencyMessage(
                            "Equation discovery returned an incomplete result (missing observed or progression profiles).",
                            stdout,
                            stderr));
                }
                result.RawJson = json;
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not EquationDiscoveryStageValidationException)
            {
                if (IsLaunchResolutionError(ex))
                {
                    launchErrors.Add($"{candidate.FileName} {string.Join(' ', candidate.ArgumentsPrefix)}".Trim());
                    continue;
                }
                lastError = ex;
            }
        }

        if (launchErrors.Count > 0 && lastError is null)
        {
            throw new InvalidOperationException(
                $"Equation discovery could not find a usable Python interpreter. Tried: {string.Join(", ", launchErrors.Distinct(StringComparer.OrdinalIgnoreCase))}");
        }

        throw lastError ?? new InvalidOperationException("Equation discovery could not start because no Python interpreter was available.");
    }

    private static string BuildResultConsistencyMessage(string message, string stdout, string stderr)
    {
        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(details)) return message;
        details = details.Trim();
        if (details.Length > 1200) details = details[..1200] + "…";
        return $"{message}{Environment.NewLine}{Environment.NewLine}{details}";
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

        var candidates = new List<(string FileName, string[] ArgumentsPrefix)>();
        foreach (var resolved in ResolveUnixPythonPaths())
        {
            candidates.Add((resolved, Array.Empty<string>()));
        }

        candidates.Add(("/usr/bin/env", new[] { "python3" }));
        return candidates;
    }

    private static IEnumerable<string> ResolveUnixPythonPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumeratePathMatches("python3"))
        {
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        var commonPaths = new[]
        {
            "/opt/homebrew/bin/python3",
            "/usr/local/bin/python3",
            "/usr/bin/python3",
            "/Library/Frameworks/Python.framework/Versions/Current/bin/python3"
        };

        foreach (var candidate in commonPaths)
        {
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        const string frameworkRoot = "/Library/Frameworks/Python.framework/Versions";
        if (!Directory.Exists(frameworkRoot)) yield break;

        foreach (var versionDir in Directory.GetDirectories(frameworkRoot)
                     .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(versionDir, "bin", "python3");
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumeratePathMatches(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, executableName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool IsLaunchResolutionError(Exception ex) =>
        ex is Win32Exception ||
        ex is FileNotFoundException ||
        ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMissingUnixPythonAlias(string stderr, string stdout)
    {
        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return details.Contains("env: python: No such file or directory", StringComparison.OrdinalIgnoreCase) ||
               details.Contains("python: No such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static EquationDiscoveryStageValidation? ParseStageValidation(JsonElement root)
    {
        if (!root.TryGetProperty("stageValidation", out var validationElement) || validationElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new EquationDiscoveryStageValidation
        {
            ValidatorAvailable = ReadBoolean(validationElement, "validatorAvailable", true),
            Skipped = ReadBoolean(validationElement, "skipped", false),
            ConfidenceScore = ReadDouble(validationElement, "confidenceScore"),
            HeightTrend = ReadDouble(validationElement, "heightTrend"),
            BimodalityTrend = ReadDouble(validationElement, "bimodalityTrend"),
            WidthTrend = ReadDouble(validationElement, "widthTrend"),
            OverallConsistency = ReadDouble(validationElement, "overallConsistency"),
            ProblematicIndices = ReadIntArray(validationElement, "problematicIndices"),
            Rationale = ReadString(validationElement, "rationale"),
            Recommendation = ReadString(validationElement, "recommendation"),
            Interpretation = ReadString(validationElement, "interpretation"),
            Report = ReadString(validationElement, "report")
        };
    }

    private static string BuildLowConfidenceMessage(EquationDiscoveryStageValidation validation, string? details = null)
    {
        var problematic = validation.ProblematicIndices.Count == 0
            ? "none listed"
            : string.Join(", ", validation.ProblematicIndices);
        var message =
            $"Stage-ordering confidence is {validation.ConfidenceScore:P0}, which is below the 50% safety gate for pseudo-time equation discovery.{Environment.NewLine}" +
            $"{validation.Rationale}{Environment.NewLine}" +
            $"Problematic indices: {problematic}.{Environment.NewLine}" +
            $"{validation.Interpretation}";

        return string.IsNullOrWhiteSpace(details)
            ? message
            : $"{message}{Environment.NewLine}{Environment.NewLine}{details}";
    }

    private static async Task<bool> ConfirmMediumConfidenceAsync(EquationDiscoveryStageValidation validation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            return true;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ConfirmMediumConfidenceAsync(validation, cancellationToken));
        }

        return await ShowStageValidationDialogAsync(desktop.MainWindow, validation, cancellationToken);
    }

    private static async Task<bool> ShowStageValidationDialogAsync(Window owner, EquationDiscoveryStageValidation validation, CancellationToken cancellationToken)
    {
        var problematic = validation.ProblematicIndices.Count == 0
            ? "No specific indices were flagged."
            : $"Problematic indices: {string.Join(", ", validation.ProblematicIndices)}.";
        var bodyText =
            $"Stage-ordering confidence is {validation.ConfidenceScore:P0}, which falls in the caution range (50%–80%).{Environment.NewLine}{Environment.NewLine}" +
            $"{validation.Rationale}{Environment.NewLine}{validation.Interpretation}{Environment.NewLine}{problematic}{Environment.NewLine}{Environment.NewLine}" +
            "Proceed with discovery anyway?";

        var proceedButton = new Button
        {
            Content = "Proceed",
            MinWidth = 96
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 96
        };

        var dialog = new Window
        {
            Title = "Stage Validation Warning",
            CanResize = false,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = bodyText,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(validation.Report) ? string.Empty : validation.Report,
                            TextWrapping = TextWrapping.Wrap,
                            MaxHeight = 220
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                cancelButton,
                                proceedButton
                            }
                        }
                    }
                }
            }
        };

        proceedButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner);
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return 0.0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value) ? value : 0.0;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return fallback;
        return property.ValueKind == JsonValueKind.True || (property.ValueKind != JsonValueKind.False && fallback);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var values = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value))
            {
                values.Add(value);
            }
        }
        return values;
    }
}
