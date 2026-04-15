using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;

namespace PiecrustAnalyser.CSharp.Services;

public static class RoiExporter
{
    public static void ExportRoi(List<Point> vertices, string tiffPath, double pixelSizeUm, double thicknessUm)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentException.ThrowIfNullOrWhiteSpace(tiffPath);

        var sidecarPath = Path.ChangeExtension(tiffPath, ".roi.json");
        var payload = new RoiSidecarPayload
        {
            Vertices = vertices
                .Select(vertex => new RoiVertex { X = vertex.X, Y = vertex.Y })
                .ToArray(),
            PixelSizeUm = pixelSizeUm,
            ThicknessUm = thicknessUm
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(sidecarPath, json);
    }

    private sealed class RoiSidecarPayload
    {
        [JsonPropertyName("vertices")]
        public RoiVertex[] Vertices { get; init; } = Array.Empty<RoiVertex>();

        [JsonPropertyName("pixel_size_um")]
        public double PixelSizeUm { get; init; }

        [JsonPropertyName("thickness_um")]
        public double ThicknessUm { get; init; }
    }

    private sealed class RoiVertex
    {
        [JsonPropertyName("x")]
        public double X { get; init; }

        [JsonPropertyName("y")]
        public double Y { get; init; }
    }
}
