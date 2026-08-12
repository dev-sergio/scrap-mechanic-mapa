using System.IO;
using System.Text.Json;

namespace ScrapMap.Desktop;

internal static class TerrainOverlayLoader
{
    private const string AssetHost = "https://appassets.scrapmap";

    public static TerrainOverlayData? TryLoad(int seed)
    {
        var assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Terrain");
        var metadataPath = Path.Combine(assetDirectory, $"{seed}-terrain.json");
        var imagePath = Path.Combine(assetDirectory, $"{seed}-world.png");
        if (!File.Exists(metadataPath) || !File.Exists(imagePath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            var width = root.GetProperty("width").GetInt32();
            var height = root.GetProperty("height").GetInt32();
            var cellPixelSize = root.GetProperty("cellPixelSize").GetInt32();
            var worldXMin = root.GetProperty("worldXMin").GetInt32();
            var worldXMax = root.GetProperty("worldXMax").GetInt32();
            var worldYMin = root.GetProperty("worldYMin").GetInt32();
            var worldYMax = root.GetProperty("worldYMax").GetInt32();
            if (width <= 0 || height <= 0 || cellPixelSize <= 0) return null;

            return new TerrainOverlayData(
                $"{AssetHost}/Assets/Terrain/{seed}-world.png",
                width,
                height,
                cellPixelSize,
                worldXMin,
                worldXMax,
                worldYMin,
                worldYMax,
                (worldXMax - worldXMin + 1) * (worldYMax - worldYMin + 1));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record TerrainOverlayData(
    string WorldUrl,
    int Width,
    int Height,
    int CellPixelSize,
    int WorldXMin,
    int WorldXMax,
    int WorldYMin,
    int WorldYMax,
    int WorldCellCount);
