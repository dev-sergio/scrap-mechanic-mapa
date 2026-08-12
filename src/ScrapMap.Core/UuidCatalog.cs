using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScrapMap.Core;

public sealed partial class UuidCatalog
{
    private readonly IReadOnlyDictionary<string, string> _names;

    private UuidCatalog(IReadOnlyDictionary<string, string> names)
    {
        _names = names;
    }

    public int Count => _names.Count;

    public string GetName(string uuid) => _names.GetValueOrDefault(uuid, "unknown");

    public static UuidCatalog Load(string? gameRoot)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gameRoot)) return new UuidCatalog(names);

        var scriptsRoot = Path.Combine(gameRoot, "Survival", "Scripts");
        if (!Directory.Exists(scriptsRoot)) return new UuidCatalog(names);

        foreach (var file in Directory.EnumerateFiles(scriptsRoot, "*.lua", SearchOption.AllDirectories))
        {
            foreach (Match match in UuidDefinitionRegex().Matches(File.ReadAllText(file)))
            {
                names.TryAdd(
                    match.Groups["uuid"].Value.ToLowerInvariant(),
                    match.Groups["name"].Value);
            }
        }

        foreach (var databaseRoot in GetDatabaseRoots(gameRoot))
        {
            if (!Directory.Exists(databaseRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(databaseRoot, "*", SearchOption.AllDirectories)
                         .Where(IsUuidDatabaseFile))
            {
                LoadJsonUuidDefinitions(file, names);
            }
        }

        return new UuidCatalog(names);
    }

    private static IEnumerable<string> GetDatabaseRoots(string gameRoot)
    {
        foreach (var contentRoot in new[] { "Data", "Survival", "ChallengeData" })
        {
            yield return Path.Combine(gameRoot, contentRoot, "Harvestables", "Database");
            yield return Path.Combine(gameRoot, contentRoot, "Objects", "Database");
            yield return Path.Combine(gameRoot, contentRoot, "ScriptableObjects", "Database");
        }
    }

    private static bool IsUuidDatabaseFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".harvestableset", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".shapeset", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadJsonUuidDefinitions(string file, IDictionary<string, string> names)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            VisitJson(document.RootElement, names);
        }
        catch (JsonException)
        {
            // A few game database files use a JSON-like format. Lua definitions remain
            // available as the fallback when one of those files cannot be parsed.
        }
    }

    private static void VisitJson(JsonElement element, IDictionary<string, string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("uuid", out var uuidElement)
                && uuidElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(uuidElement.GetString(), out _))
            {
                var uuid = uuidElement.GetString()!.ToLowerInvariant();
                var name = ReadJsonName(element);
                if (!string.IsNullOrWhiteSpace(name)) names.TryAdd(uuid, name);
            }

            foreach (var property in element.EnumerateObject()) VisitJson(property.Value, names);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) VisitJson(item, names);
        }
    }

    private static string? ReadJsonName(JsonElement element)
    {
        if (element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            return nameElement.GetString();
        }

        if (element.TryGetProperty("script", out var scriptElement)
            && scriptElement.ValueKind == JsonValueKind.Object
            && scriptElement.TryGetProperty("class", out var classElement)
            && classElement.ValueKind == JsonValueKind.String)
        {
            return classElement.GetString();
        }

        if (element.TryGetProperty("renderable", out var renderableElement)
            && renderableElement.ValueKind == JsonValueKind.String)
        {
            var path = renderableElement.GetString()!.Replace('\\', '/');
            return Path.GetFileNameWithoutExtension(path);
        }

        return null;
    }

    [GeneratedRegex("(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*sm\\.uuid\\.new\\s*\\(\\s*\"(?<uuid>[0-9a-fA-F-]{36})\"")]
    private static partial Regex UuidDefinitionRegex();
}
