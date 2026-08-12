using ScrapMap.Core;

var gameRoot = GameLocator.FindInstallation();
var saves = SaveLocator.FindSurvivalSaves();
Console.WriteLine($"Game: {gameRoot ?? "not found"}");
Console.WriteLine($"Saves: {saves.Count}");

if (saves.Count == 0) return 1;

var catalog = UuidCatalog.Load(gameRoot);
using var safeSnapshot = await SafeSaveSnapshot.CreateAsync(saves[0].FullName);
var snapshot = await new ScrapSaveReader(catalog).ReadAsync(safeSnapshot.DatabasePath);
snapshot = snapshot with { SavePath = saves[0].FullName };
Console.WriteLine($"UUIDs: {catalog.Count}");
Console.WriteLine($"Save: {snapshot.SavePath}");
Console.WriteLine($"Version: {snapshot.Game.SavegameVersion}");
Console.WriteLine($"Seed: {snapshot.Game.Seed}");
Console.WriteLine($"Explored cells: {snapshot.ExploredCells.Count}");
if (snapshot.ExploredCells.Count > 0)
{
    var minCellX = snapshot.ExploredCells.Min(cell => cell.X);
    var maxCellX = snapshot.ExploredCells.Max(cell => cell.X);
    var minCellY = snapshot.ExploredCells.Min(cell => cell.Y);
    var maxCellY = snapshot.ExploredCells.Max(cell => cell.Y);
    var rectangleSize = (maxCellX - minCellX + 1) * (maxCellY - minCellY + 1);
    Console.WriteLine($"Cell bounds: X {minCellX}..{maxCellX}, Y {minCellY}..{maxCellY}, coverage {snapshot.ExploredCells.Count}/{rectangleSize}");
}
Console.WriteLine($"Resources: {snapshot.Resources.Count}");
Console.WriteLine($"Uncatalogued resources: {snapshot.Resources.Count(item => item.Name == "unknown")}");
foreach (var group in snapshot.Resources.GroupBy(item => item.Category).OrderByDescending(group => group.Count()))
{
    Console.WriteLine($"  {group.Key}: {group.Count()}");
}
Console.WriteLine($"Creations: {snapshot.Creations.Count}");
foreach (var group in snapshot.Creations.GroupBy(item => item.Category).OrderByDescending(group => group.Count()))
{
    Console.WriteLine($"  {group.Key}: {group.Count()}");
}
foreach (var creation in snapshot.Creations.Where(item => item.Category == "Veículos"))
{
    Console.WriteLine($"  Vehicle #{creation.Id} at ({creation.X:0.0}, {creation.Y:0.0}): {string.Join(", ", creation.Parts)}");
}

return snapshot.Resources.Count > 0 && snapshot.Creations.Count > 0 ? 0 : 2;
