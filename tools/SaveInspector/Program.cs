using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ScrapMap.Core;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: SaveInspector <save.db> [scrap-mechanic-root]");
    return 1;
}

var sourceSavePath = Path.GetFullPath(args[0]);
if (!File.Exists(sourceSavePath))
{
    Console.Error.WriteLine($"Save not found: {sourceSavePath}");
    return 2;
}

using var safeSnapshot = await SafeSaveSnapshot.CreateAsync(sourceSavePath);
var savePath = safeSnapshot.DatabasePath;

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = savePath,
    Mode = SqliteOpenMode.ReadOnly,
    Cache = SqliteCacheMode.Private
}.ToString();

await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync();

var uuidNames = args.Length == 2 ? LoadUuidNames(Path.GetFullPath(args[1])) : new Dictionary<string, string>();

Console.WriteLine($"File: {sourceSavePath} (inspected through a recovered temporary snapshot)");
Console.WriteLine($"Size: {new FileInfo(savePath).Length:N0} bytes");

foreach (var pragma in new[] { "integrity_check", "page_size", "page_count", "user_version", "application_id" })
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA {pragma};";
    Console.WriteLine($"{pragma}: {await command.ExecuteScalarAsync()}");
}

await using var schemaCommand = connection.CreateCommand();
schemaCommand.CommandText = """
    SELECT name, type, COALESCE(sql, '')
    FROM sqlite_master
    WHERE type IN ('table', 'view')
    ORDER BY type, name;
    """;

var objects = new List<(string Name, string Type, string Sql)>();
await using (var reader = await schemaCommand.ExecuteReaderAsync())
{
    while (await reader.ReadAsync())
    {
        objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
    }
}

Console.WriteLine($"Objects: {objects.Count}");
foreach (var item in objects)
{
    Console.WriteLine();
    Console.WriteLine($"[{item.Type}] {item.Name}");
    Console.WriteLine(item.Sql);

    if (item.Type == "table" && !item.Name.StartsWith("sqlite_", StringComparison.Ordinal))
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(item.Name)};";
        Console.WriteLine($"rows: {await countCommand.ExecuteScalarAsync()}");
    }
}

Console.WriteLine();
Console.WriteLine("=== GAME ===");
await PrintRowsAsync(connection, "SELECT savegameversion, flags, seed, gametick, length(mods), length(uniqueIds) FROM Game;");

Console.WriteLine();
Console.WriteLine("=== WORLD DISTRIBUTION ===");
foreach (var table in new[] { "Harvestable", "RigidBody", "ScriptData", "ScriptableObject", "Unit" })
{
    Console.WriteLine(table);
    await PrintRowsAsync(connection, $"SELECT worldId, COUNT(*) FROM {QuoteIdentifier(table)} GROUP BY worldId ORDER BY worldId;");
}

Console.WriteLine();
Console.WriteLine("=== RIGID BODY BOUNDS ===");
await PrintRowsAsync(connection, "SELECT MIN(minX), MAX(maxX), MIN(minY), MAX(maxY) FROM RigidBodyBounds;");
await PrintRowsAsync(connection, """
    SELECT b.id, b.minX, b.maxX, b.minY, b.maxY, COUNT(c.id) AS shapes, length(r.data)
    FROM RigidBodyBounds b
    JOIN RigidBody r ON r.id = b.id
    LEFT JOIN ChildShape c ON c.bodyId = b.id
    GROUP BY b.id
    ORDER BY shapes DESC, b.id
    LIMIT 20;
    """);

Console.WriteLine();
Console.WriteLine("=== BLOB LENGTHS ===");
foreach (var (table, column) in new[]
{
    ("RigidBody", "data"), ("ChildShape", "data"), ("Harvestable", "data"),
    ("ScriptData", "uid"), ("ScriptData", "key"), ("ScriptData", "data"),
    ("GenericData", "uid"), ("GenericData", "key"), ("GenericData", "data"),
    ("Tool", "data"), ("Unit", "data")
})
{
    Console.WriteLine($"{table}.{column}");
    await PrintRowsAsync(connection, $"SELECT length({QuoteIdentifier(column)}), COUNT(*) FROM {QuoteIdentifier(table)} GROUP BY length({QuoteIdentifier(column)}) ORDER BY COUNT(*) DESC, 1 LIMIT 12;");
}

Console.WriteLine();
Console.WriteLine("=== BLOB SAMPLES ===");
foreach (var (table, columns) in new[]
{
    ("RigidBody", "id, worldId, hex(data)"),
    ("ChildShape", "id, bodyId, hex(data)"),
    ("Harvestable", "id, worldId, x, y, size, hex(data)"),
    ("ScriptData", "hex(uid), hex(key), worldId, flags, hex(data)"),
    ("GenericData", "hex(uid), hex(key), worldId, flags, hex(data)"),
    ("Tool", "id, hex(data)"),
    ("Unit", "id, worldId, x, y, hex(data)")
})
{
    Console.WriteLine(table);
    await PrintRowsAsync(connection, $"SELECT {columns} FROM {QuoteIdentifier(table)} LIMIT 5;", maxValueLength: table == "GenericData" ? 5000 : 240);
}

if (uuidNames.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"=== UUID CATALOG ({uuidNames.Count:N0} definitions) ===");
    await PrintChildShapeTypesAsync(connection, uuidNames);
    await PrintHarvestableTypesAsync(connection, uuidNames);
}

return 0;

static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

static async Task PrintRowsAsync(SqliteConnection connection, string sql, int maxValueLength = 120)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var values = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            values[i] = value.Length <= maxValueLength ? value : value[..maxValueLength] + "…";
        }
        Console.WriteLine(string.Join(" | ", values));
    }
}

static Dictionary<string, string> LoadUuidNames(string gameRoot)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var scriptsRoot = Path.Combine(gameRoot, "Survival", "Scripts");
    if (!Directory.Exists(scriptsRoot))
    {
        Console.Error.WriteLine($"Survival scripts not found: {scriptsRoot}");
        return result;
    }

    var regex = new Regex("(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*sm\\.uuid\\.new\\s*\\(\\s*\"(?<uuid>[0-9a-fA-F-]{36})\"", RegexOptions.Compiled);
    foreach (var file in Directory.EnumerateFiles(scriptsRoot, "*.lua", SearchOption.AllDirectories))
    {
        foreach (Match match in regex.Matches(File.ReadAllText(file)))
        {
            result.TryAdd(match.Groups["uuid"].Value.ToLowerInvariant(), match.Groups["name"].Value);
        }
    }
    return result;
}

static async Task PrintChildShapeTypesAsync(SqliteConnection connection, IReadOnlyDictionary<string, string> uuidNames)
{
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var bodyIdMismatches = 0;
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT bodyId, data FROM ChildShape;";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var expectedBodyId = reader.GetInt64(0);
        var data = (byte[])reader[1];
        if (data.Length < 27) continue;
        var bodyId = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(7, 4));
        if (bodyId != expectedBodyId) bodyIdMismatches++;
        var uuid = FormatReversedUuid(data.AsSpan(11, 16));
        counts[uuid] = counts.GetValueOrDefault(uuid) + 1;
    }

    Console.WriteLine($"ChildShape body-id mismatches: {bodyIdMismatches}");
    Console.WriteLine("Top ChildShape UUIDs:");
    foreach (var item in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(30))
    {
        Console.WriteLine($"{item.Value,5} | {item.Key} | {uuidNames.GetValueOrDefault(item.Key, "<unknown>")}");
    }
}

static async Task PrintHarvestableTypesAsync(SqliteConnection connection, IReadOnlyDictionary<string, string> uuidNames)
{
    var offsets = new Dictionary<int, int>();
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var coordinateSamples = new List<string>();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT id, x, y, data FROM Harvestable;";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var id = reader.GetInt64(0);
        var cellX = reader.GetInt64(1);
        var cellY = reader.GetInt64(2);
        var data = (byte[])reader[3];
        for (var offset = 0; offset <= data.Length - 16; offset++)
        {
            var uuid = FormatReversedUuid(data.AsSpan(offset, 16));
            if (!uuidNames.ContainsKey(uuid)) continue;
            offsets[offset] = offsets.GetValueOrDefault(offset) + 1;
            counts[uuid] = counts.GetValueOrDefault(uuid) + 1;
            if (coordinateSamples.Count < 12 && data.Length >= offset + 28)
            {
                var z = ReadSingleBigEndian(data.AsSpan(offset + 16, 4));
                var y = ReadSingleBigEndian(data.AsSpan(offset + 20, 4));
                var x = ReadSingleBigEndian(data.AsSpan(offset + 24, 4));
                coordinateSamples.Add($"id={id} cell=({cellX},{cellY}) pos=({x:0.##},{y:0.##},{z:0.##}) uuid={uuidNames[uuid]}");
            }
            break;
        }
    }

    Console.WriteLine("Harvestable UUID offsets: " + string.Join(", ", offsets.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}")));
    Console.WriteLine("Top Harvestable UUIDs:");
    foreach (var item in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(30))
    {
        Console.WriteLine($"{item.Value,5} | {item.Key} | {uuidNames.GetValueOrDefault(item.Key, "<unknown>")}");
    }
    Console.WriteLine("Harvestable coordinate samples:");
    foreach (var sample in coordinateSamples) Console.WriteLine(sample);
}

static string FormatReversedUuid(ReadOnlySpan<byte> bytes)
{
    Span<byte> reversed = stackalloc byte[16];
    for (var i = 0; i < 16; i++) reversed[i] = bytes[15 - i];
    var hex = Convert.ToHexString(reversed).ToLowerInvariant();
    return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
}

static float ReadSingleBigEndian(ReadOnlySpan<byte> bytes)
{
    var bits = BinaryPrimitives.ReadInt32BigEndian(bytes);
    return BitConverter.Int32BitsToSingle(bits);
}
