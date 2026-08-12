using Microsoft.Data.Sqlite;
using ScrapMap.Core.Models;

namespace ScrapMap.Core;

public sealed class ScrapSaveReader
{
    private readonly UuidCatalog _uuidCatalog;

    public ScrapSaveReader(UuidCatalog uuidCatalog)
    {
        _uuidCatalog = uuidCatalog;
    }

    public async Task<WorldSnapshot> ReadAsync(string savePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(savePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var metadata = await ReadMetadataAsync(connection, cancellationToken);
        var exploredCells = await ReadExploredCellsAsync(connection, cancellationToken);
        var resources = await ReadResourcesAsync(connection, cancellationToken);
        var creations = await ReadCreationsAsync(connection, cancellationToken);
        return new WorldSnapshot(fullPath, metadata, exploredCells, resources, creations);
    }

    private static async Task<GameMetadata> ReadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT savegameversion, flags, seed, gametick FROM Game LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The save has no Game row.");
        }

        return new GameMetadata(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    private static async Task<IReadOnlyList<ExploredCell>> ReadExploredCellsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cells = new Dictionary<(int X, int Y), int>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT x, y, COUNT(*)
                FROM (
                    SELECT x, y FROM Harvestable WHERE worldId = 1
                    UNION ALL
                    SELECT x, y FROM Unit WHERE worldId = 1
                    UNION ALL
                    SELECT x, y FROM PathNode WHERE worldId = 1
                    UNION ALL
                    SELECT x, y FROM VoxelTerrain WHERE worldId = 1
                )
                GROUP BY x, y;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cells[(reader.GetInt32(0), reader.GetInt32(1))] = reader.GetInt32(2);
            }
        }

        await using (var boundsCommand = connection.CreateCommand())
        {
            boundsCommand.CommandText = """
                SELECT (minX + maxX) / 2.0, (minY + maxY) / 2.0
                FROM RigidBodyBounds b
                JOIN RigidBody r ON r.id = b.id
                WHERE r.worldId = 1;
                """;
            await using var reader = await boundsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var cell = (
                    X: (int)Math.Floor(reader.GetDouble(0) / 64.0),
                    Y: (int)Math.Floor(reader.GetDouble(1) / 64.0));
                cells[cell] = cells.GetValueOrDefault(cell) + 1;
            }
        }

        return cells
            .Select(item => new ExploredCell(item.Key.X, item.Key.Y, item.Value))
            .OrderBy(cell => cell.Y)
            .ThenBy(cell => cell.X)
            .ToArray();
    }

    private async Task<IReadOnlyList<ResourceMarker>> ReadResourcesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var resources = new List<ResourceMarker>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, worldId, data FROM Harvestable WHERE length(data) >= 48;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var data = (byte[])reader[2];
            const int uuidOffset = 20;
            var uuid = SaveBlobReader.ReadReversedUuid(data.AsSpan(uuidOffset, 16));
            var name = _uuidCatalog.GetName(uuid);
            var category = ClassifyResource(name, uuid);
            resources.Add(new ResourceMarker(
                reader.GetInt64(0),
                reader.GetInt64(1),
                SaveBlobReader.ReadSingleBigEndian(data.AsSpan(uuidOffset + 24, 4)),
                SaveBlobReader.ReadSingleBigEndian(data.AsSpan(uuidOffset + 20, 4)),
                SaveBlobReader.ReadSingleBigEndian(data.AsSpan(uuidOffset + 16, 4)),
                uuid,
                name,
                GetResourceDisplayName(name, uuid),
                category.Name,
                category.Color));
        }

        return resources;
    }

    private async Task<IReadOnlyList<CreationMarker>> ReadCreationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var partsByBody = new Dictionary<long, HashSet<string>>();
        await using (var partCommand = connection.CreateCommand())
        {
            partCommand.CommandText = "SELECT bodyId, data FROM ChildShape WHERE length(data) >= 27;";
            await using var reader = await partCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bodyId = reader.GetInt64(0);
                var data = (byte[])reader[1];
                var uuid = SaveBlobReader.ReadReversedUuid(data.AsSpan(11, 16));
                var name = _uuidCatalog.GetName(uuid);
                if (!partsByBody.TryGetValue(bodyId, out var parts))
                {
                    parts = [];
                    partsByBody[bodyId] = parts;
                }
                parts.Add(name == "unknown" ? uuid : name);
            }
        }

        var bodies = new List<BodyRecord>();
        var links = new BodyLinks();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.worldId, b.minX, b.maxX, b.minY, b.maxY, COUNT(c.id)
            FROM RigidBody r
            JOIN RigidBodyBounds b ON b.id = r.id
            LEFT JOIN ChildShape c ON c.bodyId = r.id
            GROUP BY r.id, r.worldId, b.minX, b.maxX, b.minY, b.maxY
            ORDER BY r.id;
            """;
        await using (var boundsReader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await boundsReader.ReadAsync(cancellationToken))
            {
                var body = new BodyRecord(
                    boundsReader.GetInt64(0),
                    boundsReader.GetInt64(1),
                    boundsReader.GetDouble(2),
                    boundsReader.GetDouble(3),
                    boundsReader.GetDouble(4),
                    boundsReader.GetDouble(5),
                    boundsReader.GetInt32(6));
                bodies.Add(body);
                links.Add(body.Id);
            }
        }

        await using (var jointCommand = connection.CreateCommand())
        {
            jointCommand.CommandText = """
                SELECT a.bodyId, b.bodyId
                FROM Joint j
                JOIN ChildShape a ON a.id = j.childShapeIdA
                JOIN ChildShape b ON b.id = j.childShapeIdB;
                """;
            await using var jointReader = await jointCommand.ExecuteReaderAsync(cancellationToken);
            while (await jointReader.ReadAsync(cancellationToken))
            {
                links.Union(jointReader.GetInt64(0), jointReader.GetInt64(1));
            }
        }

        return bodies
            .GroupBy(body => links.Find(body.Id))
            .Select(group =>
            {
                var groupedBodies = group.ToArray();
                var minX = groupedBodies.Min(body => body.MinX);
                var maxX = groupedBodies.Max(body => body.MaxX);
                var minY = groupedBodies.Min(body => body.MinY);
                var maxY = groupedBodies.Max(body => body.MaxY);
                var parts = groupedBodies
                    .SelectMany(body => partsByBody.GetValueOrDefault(body.Id) ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new CreationMarker(
                    groupedBodies.Min(body => body.Id),
                    groupedBodies[0].WorldId,
                    (minX + maxX) / 2,
                    (minY + maxY) / 2,
                    minX,
                    maxX,
                    minY,
                    maxY,
                    groupedBodies.Length,
                    groupedBodies.Sum(body => body.ShapeCount),
                    ClassifyCreation(parts),
                    parts);
            })
            .OrderBy(creation => creation.Id)
            .ToArray();
    }

    private static (string Name, string Color) ClassifyResource(string name, string uuid)
    {
        if (name.Contains("tree_", StringComparison.OrdinalIgnoreCase)) return ("Árvores", "#52b788");
        if (name.Contains("stone_", StringComparison.OrdinalIgnoreCase)) return ("Pedras", "#adb5bd");
        if (name.Contains("corn", StringComparison.OrdinalIgnoreCase)) return ("Milho", "#ffd166");
        if (name.Contains("cotton", StringComparison.OrdinalIgnoreCase)) return ("Algodão", "#f8f9fa");
        if (name.Contains("pigmentflower", StringComparison.OrdinalIgnoreCase)) return ("Flores", "#ef476f");
        if (name.Contains("slimyclam", StringComparison.OrdinalIgnoreCase)) return ("Mariscos", "#4cc9f0");
        if (name.Contains("oil", StringComparison.OrdinalIgnoreCase)) return ("Petróleo", "#343a40");
        if (name.Contains("beehive", StringComparison.OrdinalIgnoreCase)) return ("Colmeias", "#ffb703");
        if (name.Contains("loot", StringComparison.OrdinalIgnoreCase)) return ("Suprimentos", "#9b5de5");
        if (name.Contains("soil", StringComparison.OrdinalIgnoreCase)) return ("Plantação", "#8d6e63");
        if (name.Contains("amber", StringComparison.OrdinalIgnoreCase)) return ("Âmbar", "#ff9f1c");
        if (name.Contains("crystal", StringComparison.OrdinalIgnoreCase)) return ("Cristais", "#72ddf7");
        if (name.Contains("mineral", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ore", StringComparison.OrdinalIgnoreCase)) return ("Minérios", "#b08968");
        if (name.Contains("gaspotato", StringComparison.OrdinalIgnoreCase)) return ("Batatas explosivas", "#e63946");
        if (name.Contains("quest", StringComparison.OrdinalIgnoreCase)) return ("Itens de missão", "#ff70a6");
        if (name.Contains("storage", StringComparison.OrdinalIgnoreCase)) return ("Armazenamento", "#577590");
        if (name.Contains("remains", StringComparison.OrdinalIgnoreCase)) return ("Restos", "#6c757d");
        if (name.Contains("trap", StringComparison.OrdinalIgnoreCase)) return ("Armadilhas", "#d00000");
        if (name.Contains("fence", StringComparison.OrdinalIgnoreCase)) return ("Cercas", "#a68a64");
        if (name.Contains("plantable", StringComparison.OrdinalIgnoreCase)) return ("Plantações", "#70e000");

        var automaticName = HumanizeIdentifier(name, uuid);
        return (automaticName, CreateStableColor(name == "unknown" ? uuid : name));
    }

    private static string GetResourceDisplayName(string name, string uuid) => name switch
    {
        var value when value.Contains("tree_", StringComparison.OrdinalIgnoreCase) => "Árvore",
        var value when value.Contains("stone_", StringComparison.OrdinalIgnoreCase) => "Pedra",
        var value when value.Contains("corn", StringComparison.OrdinalIgnoreCase) => "Milho",
        var value when value.Contains("cotton", StringComparison.OrdinalIgnoreCase) => "Algodão",
        var value when value.Contains("pigmentflower", StringComparison.OrdinalIgnoreCase) => "Flor de pigmento",
        var value when value.Contains("slimyclam", StringComparison.OrdinalIgnoreCase) => "Marisco",
        var value when value.Contains("oil", StringComparison.OrdinalIgnoreCase) => "Petróleo",
        var value when value.Contains("beehive", StringComparison.OrdinalIgnoreCase) => "Colmeia",
        var value when value.Contains("loot", StringComparison.OrdinalIgnoreCase) => "Suprimentos",
        var value when value.Contains("soil", StringComparison.OrdinalIgnoreCase) => "Plantação",
        _ => HumanizeIdentifier(name, uuid)
    };

    private static string HumanizeIdentifier(string name, string uuid)
    {
        if (name == "unknown") return $"Objeto {uuid[..8]}";

        var normalized = name;
        foreach (var prefix in new[] { "harvestable_", "hvs_", "obj_" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        return string.Join(' ', normalized
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string CreateStableColor(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return $"hsl({hash % 360}, 65%, 60%)";
    }

    private static string ClassifyCreation(IReadOnlyList<string> parts)
    {
        if (parts.Any(part => part.Contains("spaceship", StringComparison.OrdinalIgnoreCase))) return "Nave inicial";
        if (parts.Any(part => part.Contains("driverseat", StringComparison.OrdinalIgnoreCase)
            || part.Contains("steering", StringComparison.OrdinalIgnoreCase))) return "Veículos";
        if (parts.Count == 1 && parts[0].Contains("wheel", StringComparison.OrdinalIgnoreCase)) return "Peças soltas";
        return "Construções";
    }

    private sealed record BodyRecord(
        long Id,
        long WorldId,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        int ShapeCount);

    private sealed class BodyLinks
    {
        private readonly Dictionary<long, long> _parents = [];

        public void Add(long id) => _parents.TryAdd(id, id);

        public long Find(long id)
        {
            Add(id);
            if (_parents[id] != id) _parents[id] = Find(_parents[id]);
            return _parents[id];
        }

        public void Union(long left, long right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot) _parents[rightRoot] = leftRoot;
        }
    }
}
