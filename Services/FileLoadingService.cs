using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PiecrustAnalyser.CSharp.Models;
using SkiaSharp;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class FileLoadingService
{
    private readonly HeightMapDisplayService _displayService = new();

    public async Task<LoadedHeightMap?> LoadAsync(string path, double fallbackScanSizeNm)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var peek = Encoding.Latin1.GetString(bytes, 0, Math.Min(500, bytes.Length));
        var isSpm = ext is "spm" or "dat" or "nid" || Regex.IsMatch(ext, "^\\d{3}$");

        if (isSpm || peek.Contains("\\*File list") || peek.Contains("Nanoscope") || peek.Contains("\\*Ciao"))
        {
            var bruker = ParseBruker(bytes, path);
            if (bruker is not null) return _displayService.Prepare(bruker);
        }

        if (isSpm)
        {
            var raw = ParseRaw(bytes, path, fallbackScanSizeNm);
            if (raw is not null) return _displayService.Prepare(raw);
        }

        var bitmap = await ParseBitmapAsync(bytes, path, fallbackScanSizeNm).ConfigureAwait(false);
        return bitmap is null ? null : _displayService.Prepare(bitmap);
    }

    private static async Task<LoadedHeightMap?> ParseBitmapAsync(byte[] bytes, string path, double scanSizeNm)
    {
        var decoded = TryDecodeBitmapData(bytes);
        if (decoded is null && OperatingSystem.IsMacOS())
        {
            decoded = await TryDecodeBitmapWithSipsAsync(path).ConfigureAwait(false);
        }

        if (decoded is null) return null;
        var calibration = TryResolveSiblingRawCalibration(path, decoded.Value.Width, decoded.Value.Height);
        var calibratedScanSizeNm = calibration?.ScanSizeNm ?? scanSizeNm;
        var formatLabel = calibration is null
            ? decoded.Value.FormatLabel
            : $"{decoded.Value.FormatLabel} (calibrated)";

        return new LoadedHeightMap
        {
            Name = Path.GetFileName(path),
            FilePath = path,
            Format = formatLabel,
            Width = decoded.Value.Width,
            Height = decoded.Value.Height,
            ScanSizeNm = calibratedScanSizeNm,
            NmPerPixel = calibratedScanSizeNm / Math.Max(1, decoded.Value.Width),
            Data = decoded.Value.Data,
            Unit = "nm",
            ChannelDisplay = calibration is null ? "Image intensity" : $"Image intensity ({calibration.SourceLabel})",
            PreferScientificPreview = false
        };
    }

    private static (int Width, int Height, double[] Data, string FormatLabel)? TryDecodeBitmapData(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;
            var pixels = bitmap.Pixels;
            var data = new double[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                data[i] = 0.299 * pixel.Red + 0.587 * pixel.Green + 0.114 * pixel.Blue;
            }

            return (bitmap.Width, bitmap.Height, data, "image");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(int Width, int Height, double[] Data, string FormatLabel)?> TryDecodeBitmapWithSipsAsync(string path)
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), $"piecrust-import-{Guid.NewGuid():N}.png");
        try
        {
            var processStart = new ProcessStartInfo
            {
                FileName = "sips",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            processStart.ArgumentList.Add("-s");
            processStart.ArgumentList.Add("format");
            processStart.ArgumentList.Add("png");
            processStart.ArgumentList.Add(path);
            processStart.ArgumentList.Add("--out");
            processStart.ArgumentList.Add(tempOutput);

            using var process = Process.Start(processStart);
            if (process is null) return null;
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempOutput)) return null;

            var pngBytes = await File.ReadAllBytesAsync(tempOutput).ConfigureAwait(false);
            var decoded = TryDecodeBitmapData(pngBytes);
            return decoded is null ? null : (decoded.Value.Width, decoded.Value.Height, decoded.Value.Data, "image");
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempOutput)) File.Delete(tempOutput);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static LoadedHeightMap? ParseRaw(byte[] bytes, string path, double scanSizeNm)
    {
        var common = new[] { 64, 128, 256, 512, 1024, 2048 };
        int side = 0;
        var bytesPerPixel = 2;
        foreach (var s in common)
        {
            if (s * s * 2 <= bytes.Length)
            {
                side = s;
                bytesPerPixel = 2;
            }
            if (s * s * 4 <= bytes.Length)
            {
                side = s;
                bytesPerPixel = 4;
            }
        }
        if (side < 64) return null;

        var data = new double[side * side];
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = bytesPerPixel == 2 ? br.ReadInt16() : br.ReadSingle();
        }

        return new LoadedHeightMap
        {
            Name = Path.GetFileName(path),
            FilePath = path,
            Format = "raw",
            Width = side,
            Height = side,
            ScanSizeNm = scanSizeNm,
            NmPerPixel = scanSizeNm / Math.Max(1, side),
            Data = data,
            Unit = "nm",
            ChannelDisplay = "Raw height",
            PreferScientificPreview = true
        };
    }

    private static LoadedHeightMap? ParseBruker(byte[] bytes, string path)
    {
        var previewLength = Math.Min(16384, bytes.Length);
        var preview = Encoding.Latin1.GetString(bytes, 0, previewLength);
        var headerLengthMatch = Regex.Match(preview, "\\\\\\*File list[\\s\\S]{0,4096}?\\\\Data length:\\s*(\\d+)", RegexOptions.IgnoreCase);
        var declaredHeaderLength = headerLengthMatch.Success ? int.Parse(headerLengthMatch.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        var headerLength = Math.Min(bytes.Length, declaredHeaderLength is > 0 ? declaredHeaderLength.Value : 131072);
        var header = Encoding.Latin1.GetString(bytes, 0, headerLength);
        var lines = header.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var channels = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("\\*Ciao image list", StringComparison.OrdinalIgnoreCase) || line.StartsWith("\\*Image list", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                channels.Add(current);
                continue;
            }

            var match = Regex.Match(line, "^\\\\([^:]+(?::[^:]+)?):\\s*(.*)$");
            if (!match.Success) continue;
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();
            if (current is null) globals[key] = value;
            else current[key] = value;
        }

        var channel = PickBrukerHeightChannel(channels);
        if (channel is null) return null;

        int ParseInt(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (channel.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            }
            return 0;
        }

        var width = ParseInt("Samps/line", "samps/line");
        var height = ParseInt("Number of lines", "number of lines");
        var offset = ParseInt("Data offset", "data offset");
        var bytesPerPixel = ParseInt("Bytes/pixel", "bytes/pixel");
        if (width <= 0 || height <= 0 || offset < 0 || bytesPerPixel <= 0 || offset >= bytes.Length) return null;

        var scanSize = ParseScanSizeNm(Field(channel, "Scan Size")) ?? ParseScanSizeNm(Field(globals, "Scan Size")) ?? (width, height);
        var zSens = ParseZSensitivityNmPerV(Field(globals, "@Sens. Zsens", "Sens. Zsens"));
        var zScaleValue = Field(channel, "@2:Z scale", "@3:Z scale", "Z scale");
        var zOffsetValue = Field(channel, "@2:Z offset", "@3:Z offset", "Z offset");
        var voltsPerLsb = ParseVoltsPerLsb(zScaleValue);
        var fullScaleVolts = ParseTrailingVolts(zScaleValue);
        var offsetVolts = ParseTrailingVolts(zOffsetValue) ?? 0;
        var fullScaleCounts = Math.Pow(2, 8 * bytesPerPixel);
        double? calibrationNmPerLsb = null;
        if (voltsPerLsb is not null && zSens is not null) calibrationNmPerLsb = voltsPerLsb.Value * zSens.Value;
        else if (fullScaleVolts is not null && zSens is not null) calibrationNmPerLsb = fullScaleVolts.Value * zSens.Value / Math.Max(1, fullScaleCounts);
        var valueScale = calibrationNmPerLsb ?? 1;
        var offsetNm = zSens is not null ? offsetVolts * zSens.Value : 0;

        var count = width * height;
        var data = new double[count];
        using var ms = new MemoryStream(bytes, offset, bytes.Length - offset);
        using var br = new BinaryReader(ms);
        for (var i = 0; i < count && ms.Position <= ms.Length - bytesPerPixel; i++)
        {
            var raw = bytesPerPixel == 4 ? br.ReadInt32() : br.ReadInt16();
            data[i] = raw * valueScale + offsetNm;
        }

        var imageLabel = ImageLabel(Field(channel, "@2:Image Data", "@3:Image Data", "Image Data"));
        return new LoadedHeightMap
        {
            Name = Path.GetFileName(path),
            FilePath = path,
            Format = "Bruker",
            Width = width,
            Height = height,
            ScanSizeNm = scanSize.X,
            NmPerPixel = scanSize.X / Math.Max(1, width),
            Data = data,
            Unit = "nm",
            ChannelDisplay = imageLabel,
            PreferScientificPreview = true
        };
    }

    private static Dictionary<string, string>? PickBrukerHeightChannel(List<Dictionary<string, string>> channels)
    {
        return channels
            .Select((channel, index) => new
            {
                Channel = channel,
                Index = index,
                Label = ImageLabel(Field(channel, "@2:Image Data", "@3:Image Data", "Image Data")).ToLowerInvariant(),
                LineDirection = Field(channel, "Line Direction").ToLowerInvariant(),
                ScanLine = Field(channel, "Scan Line").ToLowerInvariant()
            })
            .Select(entry => new
            {
                entry.Channel,
                entry.Index,
                Score = (entry.Label.Contains("height") ? 100 : 0)
                      - (entry.Label.Contains("error") ? 80 : 0)
                      + (entry.ScanLine == "main" ? 4 : 0)
                      + (entry.LineDirection == "trace" ? 10 : entry.LineDirection == "retrace" ? 6 : 0)
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Channel)
            .FirstOrDefault();
    }

    private static string Field(Dictionary<string, string> section, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (section.TryGetValue(key, out var value)) return value;
        }
        return string.Empty;
    }

    private static string ImageLabel(string raw)
    {
        var quoted = Regex.Match(raw ?? string.Empty, "\"([^\"]+)\"");
        if (quoted.Success) return quoted.Groups[1].Value.Trim();
        var bracket = Regex.Match(raw ?? string.Empty, "\\[([^\\]]+)\\]");
        if (bracket.Success) return bracket.Groups[1].Value.Trim();
        return (raw ?? string.Empty).Trim();
    }

    private static (double X, double Y)? ParseScanSizeNm(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var pair = Regex.Match(raw, "([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s+([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s*(nm|um|µm)", RegexOptions.IgnoreCase);
        if (pair.Success)
        {
            var unit = pair.Groups[3].Value;
            return (ToNm(double.Parse(pair.Groups[1].Value, CultureInfo.InvariantCulture), unit), ToNm(double.Parse(pair.Groups[2].Value, CultureInfo.InvariantCulture), unit));
        }
        var single = Regex.Match(raw, "([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s*(nm|um|µm)", RegexOptions.IgnoreCase);
        if (!single.Success) return null;
        var value = ToNm(double.Parse(single.Groups[1].Value, CultureInfo.InvariantCulture), single.Groups[2].Value);
        return (value, value);
    }

    private static double ToNm(double value, string unit) => unit.Equals("nm", StringComparison.OrdinalIgnoreCase) ? value : value * 1000;

    private static double? ParseZSensitivityNmPerV(string raw)
    {
        var match = Regex.Match(raw ?? string.Empty, "([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s*(nm|um|µm)/V", RegexOptions.IgnoreCase);
        return match.Success ? ToNm(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value) : null;
    }

    private static double? ParseVoltsPerLsb(string raw)
    {
        var match = Regex.Match(raw ?? string.Empty, "\\(([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s*V/LSB\\)", RegexOptions.IgnoreCase);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static double? ParseTrailingVolts(string raw)
    {
        var match = Regex.Match(raw ?? string.Empty, "\\)\\s*([-+]?\\d*\\.?\\d+(?:[eE][-+]?\\d+)?)\\s*V\\b", RegexOptions.IgnoreCase);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private sealed class RawCalibrationInfo
    {
        public double ScanSizeNm { get; init; }
        public string SourceLabel { get; init; } = "raw AFM";
    }

    private static RawCalibrationInfo? TryResolveSiblingRawCalibration(string imagePath, int width, int height)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(imagePath);
            if (string.IsNullOrWhiteSpace(stem)) return null;

            var directories = new[]
            {
                Path.GetDirectoryName(imagePath),
                Directory.GetParent(Path.GetDirectoryName(imagePath) ?? string.Empty)?.FullName
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>();

            foreach (var directory in directories)
            {
                var candidates = Directory.EnumerateFiles(directory, $"{stem}.*", SearchOption.TopDirectoryOnly)
                    .Where(candidate =>
                    {
                        var ext = Path.GetExtension(candidate).TrimStart('.').ToLowerInvariant();
                        return ext is "spm" or "dat" or "nid" || Regex.IsMatch(ext, "^\\d{3}$");
                    })
                    .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase);

                foreach (var candidate in candidates)
                {
                    var loaded = TryLoadCalibrationSource(candidate);
                    if (loaded is null) continue;
                    if (loaded.Width > 0 && loaded.Height > 0 && (loaded.Width != width || loaded.Height != height))
                    {
                        continue;
                    }

                    return new RawCalibrationInfo
                    {
                        ScanSizeNm = loaded.ScanSizeNm,
                        SourceLabel = Path.GetFileName(candidate)
                    };
                }
            }
        }
        catch
        {
            // Best-effort calibration only.
        }

        return null;
    }

    private static LoadedHeightMap? TryLoadCalibrationSource(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bruker = ParseBruker(bytes, path);
            if (bruker is not null) return bruker;

            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var isRaw = ext is "spm" or "dat" or "nid" || Regex.IsMatch(ext, "^\\d{3}$");
            return isRaw ? ParseRaw(bytes, path, 500) : null;
        }
        catch
        {
            return null;
        }
    }
}
