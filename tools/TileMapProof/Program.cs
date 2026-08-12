using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScrapMap.Core;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: TileMapProof <game.log> <save.db> <game-root> <output-dir>");
    return 1;
}

var logPath = Path.GetFullPath(args[0]);
var savePath = Path.GetFullPath(args[1]);
var gameRoot = Path.GetFullPath(args[2]);
var outputDirectory = Path.GetFullPath(args[3]);
Directory.CreateDirectory(outputDirectory);

var layout = ReadLayout(logPath);
if (layout.Count == 0)
{
    Console.Error.WriteLine("No ScrapMap tile records were found in the log.");
    return 2;
}

var tileRoot = Path.Combine(gameRoot, "Survival", "Terrain", "Tiles");
var metadata = LoadTileMetadata(tileRoot);
var pngPaths = Directory.EnumerateFiles(tileRoot, "*.png", SearchOption.AllDirectories)
    .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

using var safeSnapshot = await SafeSaveSnapshot.CreateAsync(savePath);
var snapshot = await new ScrapSaveReader(UuidCatalog.Load(gameRoot)).ReadAsync(safeSnapshot.DatabasePath);
var explored = snapshot.ExploredCells.Select(cell => (cell.X, cell.Y)).ToHashSet();

var matched = layout.Count(cell => pngPaths.ContainsKey(cell.Uuid));
var exploredLayout = layout.Count(cell => explored.Contains((cell.X, cell.Y)));
Console.WriteLine($"Layout records: {layout.Count}");
Console.WriteLine($"Unique UUIDs: {layout.Select(cell => cell.Uuid).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
Console.WriteLine($"PNG matches: {matched}/{layout.Count}");
Console.WriteLine($"Explored cells represented: {exploredLayout}/{explored.Count}");

var jsonPath = Path.Combine(outputDirectory, "tile-layout.json");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new
{
    sourceSave = savePath,
    snapshot.Game.Seed,
    exploredCellCount = explored.Count,
    cells = layout.Select(cell => new
    {
        cell.X,
        cell.Y,
        uuid = cell.Uuid,
        cell.Rotation,
        cell.OffsetX,
        cell.OffsetY,
        explored = explored.Contains((cell.X, cell.Y)),
        size = metadata.GetValueOrDefault(cell.Uuid)?.CellsX ?? 1
    })
}, new JsonSerializerOptions { WriteIndented = true }));

var imagePath = Path.Combine(outputDirectory, "mapa-tiles-reais.png");
var regionImagePath = Path.Combine(outputDirectory, "mapa-regiao-extraida.png");
var extractedRegion = layout.Select(cell => (cell.X, cell.Y)).ToHashSet();
RenderMap(imagePath, layout, explored, extractedRegion, metadata, pngPaths);
RenderMap(regionImagePath, layout, extractedRegion, extractedRegion, metadata, pngPaths);
Console.WriteLine($"JSON: {jsonPath}");
Console.WriteLine($"Image: {imagePath}");
Console.WriteLine($"Region image: {regionImagePath}");
return matched == layout.Count && exploredLayout == explored.Count ? 0 : 3;

static List<TileCell> ReadLayout(string logPath)
{
    var regex = new Regex(
        @"\[SCRAPMAP_CELL\]\|(?<x>-?\d+)\|(?<y>-?\d+)\|(?<uuid>[0-9a-fA-F-]{36})\|(?<rotation>-?\d+)\|(?<offsetX>-?\d+)\|(?<offsetY>-?\d+)",
        RegexOptions.Compiled);
    var result = new Dictionary<(int X, int Y), TileCell>();
    foreach (var line in File.ReadLines(logPath))
    {
        var match = regex.Match(line);
        if (!match.Success) continue;
        var cell = new TileCell(
            int.Parse(match.Groups["x"].Value),
            int.Parse(match.Groups["y"].Value),
            match.Groups["uuid"].Value.ToLowerInvariant(),
            int.Parse(match.Groups["rotation"].Value),
            int.Parse(match.Groups["offsetX"].Value),
            int.Parse(match.Groups["offsetY"].Value));
        result[(cell.X, cell.Y)] = cell;
    }
    return result.Values.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
}

static Dictionary<string, TileMetadata> LoadTileMetadata(string tileRoot)
{
    var result = new Dictionary<string, TileMetadata>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in Directory.EnumerateFiles(tileRoot, "*.tileson", SearchOption.AllDirectories))
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var info = document.RootElement.GetProperty("info");
            var uuid = info.GetProperty("uuid").GetString();
            if (string.IsNullOrWhiteSpace(uuid)) continue;
            result[uuid] = new TileMetadata(
                info.GetProperty("cellsX").GetInt32(),
                info.GetProperty("cellsY").GetInt32());
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            // A malformed editor file does not prevent other installed tiles from rendering.
        }
    }
    return result;
}

static void RenderMap(
    string outputPath,
    IReadOnlyCollection<TileCell> layout,
    HashSet<(int X, int Y)> visibleCells,
    HashSet<(int X, int Y)> canvasCells,
    IReadOnlyDictionary<string, TileMetadata> metadata,
    IReadOnlyDictionary<string, string> pngPaths)
{
    const int cellSize = 96;
    const int padding = 24;
    var minCellX = canvasCells.Min(cell => cell.X);
    var maxCellX = canvasCells.Max(cell => cell.X);
    var minCellY = canvasCells.Min(cell => cell.Y);
    var maxCellY = canvasCells.Max(cell => cell.Y);
    var width = (maxCellX - minCellX + 1) * cellSize + padding * 2;
    var height = (maxCellY - minCellY + 1) * cellSize + padding * 2;

    using var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(output);
    graphics.Clear(Color.FromArgb(255, 28, 31, 36));
    graphics.CompositingMode = CompositingMode.SourceOver;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.SmoothingMode = SmoothingMode.HighQuality;

    using var exploredPath = new GraphicsPath(FillMode.Winding);
    foreach (var cell in visibleCells)
    {
        var left = (cell.X - minCellX) * cellSize + padding;
        var top = (cell.Y - minCellY) * cellSize + padding;
        exploredPath.AddRectangle(new Rectangle(left, top, cellSize, cellSize));
    }
    graphics.SetClip(exploredPath, CombineMode.Replace);
    using var waterBrush = new SolidBrush(Color.FromArgb(255, 10, 132, 170));
    graphics.FillRectangle(waterBrush, 0, 0, width, height);

    var imageCache = new Dictionary<(string Uuid, int Rotation, int Size), Bitmap>();
    try
    {
        foreach (var cell in layout.OrderBy(item => item.Y).ThenBy(item => item.X))
        {
            if (!pngPaths.ContainsKey(cell.Uuid)) continue;
            var size = metadata.GetValueOrDefault(cell.Uuid)?.CellsX ?? 1;
            var placement = ToPlacement(cell, size);
            var cacheKey = (cell.Uuid, placement.Rotation, size);
            if (!imageCache.TryGetValue(cacheKey, out var preview))
            {
                using var loaded = new Bitmap(pngPaths[cell.Uuid]);
                using var transparent = CopyWithTransparentBackground(loaded);
                preview = FlattenDiamond(transparent, placement.Rotation, size * cellSize);
                imageCache[cacheKey] = preview;
            }

            var sourceX = (cell.X - placement.OriginX) * cellSize;
            var sourceY = (cell.Y - placement.OriginY) * cellSize;
            var destinationX = (cell.X - minCellX) * cellSize + padding;
            var destinationY = (cell.Y - minCellY) * cellSize + padding;
            graphics.DrawImage(
                preview,
                new Rectangle(destinationX, destinationY, cellSize, cellSize),
                new Rectangle(sourceX, sourceY, cellSize, cellSize),
                GraphicsUnit.Pixel);
        }
    }
    finally
    {
        foreach (var image in imageCache.Values) image.Dispose();
    }
    graphics.ResetClip();
    output.Save(outputPath, ImageFormat.Png);
}

static TilePlacement ToPlacement(TileCell cell, int size)
{
    var rotation = ((cell.Rotation % 4) + 4) % 4;
    var (dx, dy) = rotation switch
    {
        0 => (cell.OffsetX, cell.OffsetY),
        1 => (size - 1 - cell.OffsetY, cell.OffsetX),
        2 => (size - 1 - cell.OffsetX, size - 1 - cell.OffsetY),
        _ => (cell.OffsetY, size - 1 - cell.OffsetX)
    };
    return new TilePlacement(cell.Uuid, rotation, cell.X - dx, cell.Y - dy, size);
}

static unsafe Bitmap CopyWithTransparentBackground(Bitmap source)
{
    var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(source, 0, 0);
    var corner = result.GetPixel(0, 0);
    var exterior = new bool[result.Width * result.Height];
    var pending = new Queue<Point>();

    void TryEnqueue(int x, int y)
    {
        if (x < 0 || x >= result.Width || y < 0 || y >= result.Height) return;
        var index = y * result.Width + x;
        if (exterior[index]) return;
        var pixel = result.GetPixel(x, y);
        var backgroundDistance =
            Math.Abs(pixel.B - corner.B) +
            Math.Abs(pixel.G - corner.G) +
            Math.Abs(pixel.R - corner.R);
        if (backgroundDistance >= 30) return;
        exterior[index] = true;
        pending.Enqueue(new Point(x, y));
    }

    for (var x = 0; x < result.Width; x++)
    {
        TryEnqueue(x, 0);
        TryEnqueue(x, result.Height - 1);
    }
    for (var y = 0; y < result.Height; y++)
    {
        TryEnqueue(0, y);
        TryEnqueue(result.Width - 1, y);
    }
    while (pending.TryDequeue(out var point))
    {
        TryEnqueue(point.X - 1, point.Y);
        TryEnqueue(point.X + 1, point.Y);
        TryEnqueue(point.X, point.Y - 1);
        TryEnqueue(point.X, point.Y + 1);
    }

    var rectangle = new Rectangle(0, 0, result.Width, result.Height);
    var data = result.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    try
    {
        for (var y = 0; y < result.Height; y++)
        {
            var row = (byte*)data.Scan0 + y * data.Stride;
            for (var x = 0; x < result.Width; x++)
            {
                if (exterior[y * result.Width + x]) row[x * 4 + 3] = 0;
            }
        }
    }
    finally
    {
        result.UnlockBits(data);
    }
    return result;
}

static unsafe Bitmap FlattenDiamond(Bitmap source, int quarterTurns, int targetSize)
{
    var turns = ((quarterTurns % 4) + 4) % 4;
    var result = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppArgb);
    var sourceRectangle = new Rectangle(0, 0, source.Width, source.Height);
    var resultRectangle = new Rectangle(0, 0, result.Width, result.Height);
    var sourceData = source.LockBits(sourceRectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var resultData = result.LockBits(resultRectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
        // The installed previews share a 220x150 canvas. Their ground diamond uses
        // approximately (4,94), (110,41), (216,94), (110,147). Sampling that
        // diamond into a square produces a readable north-up map instead of stacking
        // hundreds of miniature isometric scenes on top of one another.
        var centerX = source.Width / 2.0;
        var diamondTop = source.Height * (41.0 / 150.0);
        var diamondHalfWidth = source.Width * (106.0 / 220.0);
        var diamondHalfHeight = source.Height * (53.0 / 150.0);
        for (var y = 0; y < result.Height; y++)
        {
            var resultRow = (byte*)resultData.Scan0 + y * resultData.Stride;
            for (var x = 0; x < result.Width; x++)
            {
                var u = (x + 0.5) / result.Width;
                var v = (y + 0.5) / result.Height;
                (u, v) = turns switch
                {
                    1 => (v, 1.0 - u),
                    2 => (1.0 - u, 1.0 - v),
                    3 => (1.0 - v, u),
                    _ => (u, v)
                };
                var sourceX = (int)Math.Floor(centerX + (u - v) * diamondHalfWidth);
                var sourceY = (int)Math.Floor(diamondTop + (u + v) * diamondHalfHeight);
                if (sourceX < 0 || sourceX >= source.Width || sourceY < 0 || sourceY >= source.Height) continue;
                var sourcePixel = (byte*)sourceData.Scan0 + sourceY * sourceData.Stride + sourceX * 4;
                var resultPixel = resultRow + x * 4;
                *(int*)resultPixel = *(int*)sourcePixel;
            }
        }
    }
    finally
    {
        source.UnlockBits(sourceData);
        result.UnlockBits(resultData);
    }
    return result;
}

sealed record TileCell(int X, int Y, string Uuid, int Rotation, int OffsetX, int OffsetY);
sealed record TileMetadata(int CellsX, int CellsY);
sealed record TilePlacement(string Uuid, int Rotation, int OriginX, int OriginY, int Size);
