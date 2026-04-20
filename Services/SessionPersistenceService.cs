using System.Text.Json;
using PiecrustAnalyser.CSharp.Models;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class SessionPersistenceService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    private string SessionPath
    {
        get
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PieCrustAnalyser");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "session.json");
        }
    }

    public SessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var json = File.ReadAllText(SessionPath);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<SessionSnapshot>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(SessionSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            File.WriteAllText(SessionPath, json);
        }
        catch
        {
            // Keep the desktop app usable even if local persistence fails.
        }
    }
}

public sealed class SessionSnapshot
{
    public int SelectedTabIndex { get; set; }
    public double EvolutionProgress { get; set; }
    public double SimulationProgress { get; set; }
    public string SelectedSequencingMode { get; set; } = "auto";
    public string SelectedGrowthModelMode { get; set; } = "current";
    public string? SelectedFilePath { get; set; }
    public string? SimulationStartFilePath { get; set; }
    public string? SimulationEndFilePath { get; set; }
    public List<FileSessionSnapshot> Files { get; set; } = new();
}

public sealed class FileSessionSnapshot
{
    public string FilePath { get; set; } = string.Empty;
    public string Stage { get; set; } = "early";
    public string ConditionType { get; set; } = "unassigned";
    public double AntibioticDoseUgPerMl { get; set; }
    public int SequenceOrder { get; set; }
    public double GuideCorridorWidthNm { get; set; }
    public string DisplayRangeMode { get; set; } = "auto";
    public double FixedDisplayMin { get; set; }
    public double FixedDisplayMax { get; set; } = 1;
    public bool GuideLineFinished { get; set; }
    public List<PointSnapshot> GuidePoints { get; set; } = new();
    public List<PointSnapshot> ProfileLine { get; set; } = new();
}

public sealed class PointSnapshot
{
    public double X { get; set; }
    public double Y { get; set; }

    public static PointSnapshot From(PointD point) => new() { X = point.X, Y = point.Y };
    public PointD ToPointD() => new(X, Y);
}
