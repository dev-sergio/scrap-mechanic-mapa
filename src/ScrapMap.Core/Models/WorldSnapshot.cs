namespace ScrapMap.Core.Models;

public sealed record WorldSnapshot(
    string SavePath,
    GameMetadata Game,
    IReadOnlyList<ExploredCell> ExploredCells,
    IReadOnlyList<ResourceMarker> Resources,
    IReadOnlyList<CreationMarker> Creations);

public sealed record GameMetadata(
    int SavegameVersion,
    int Flags,
    int Seed,
    long GameTick);

public sealed record ExploredCell(
    int X,
    int Y,
    int PersistedEntityCount);

public sealed record ResourceMarker(
    long Id,
    long WorldId,
    float X,
    float Y,
    float Z,
    string Uuid,
    string Name,
    string DisplayName,
    string Category,
    string Color);

public sealed record CreationMarker(
    long Id,
    long WorldId,
    double X,
    double Y,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    int BodyCount,
    int ShapeCount,
    string Category,
    IReadOnlyList<string> Parts);
