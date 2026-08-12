using System.Text.RegularExpressions;

namespace ScrapMap.Core;

public static partial class GameLocator
{
    public static string? FindInstallation()
    {
        foreach (var candidate in GetCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, "Release", "ScrapMechanic.exe")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidates()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var defaultSteam = Path.Combine(programFilesX86, "Steam");
        foreach (var steamRoot in new[] { defaultSteam, @"C:\Program Files\Steam" })
        {
            yield return Path.Combine(steamRoot, "steamapps", "common", "Scrap Mechanic");

            var librariesFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(librariesFile)) continue;
            foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(librariesFile)))
            {
                var library = match.Groups["path"].Value.Replace("\\\\", "\\");
                yield return Path.Combine(library, "steamapps", "common", "Scrap Mechanic");
            }
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}

