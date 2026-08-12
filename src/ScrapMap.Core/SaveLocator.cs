namespace ScrapMap.Core;

public static class SaveLocator
{
    public static IReadOnlyList<FileInfo> FindSurvivalSaves()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userRoot = Path.Combine(appData, "Axolot Games", "Scrap Mechanic", "User");
        if (!Directory.Exists(userRoot))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(userRoot, "*.db", SearchOption.AllDirectories)
            .Where(path => IsSurvivalSave(userRoot, path))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
    }

    private static bool IsSurvivalSave(string userRoot, string path)
    {
        var relative = Path.GetRelativePath(userRoot, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 4
            && parts[0].StartsWith("User_", StringComparison.OrdinalIgnoreCase)
            && parts.Contains("Save", StringComparer.OrdinalIgnoreCase)
            && parts.Contains("Survival", StringComparer.OrdinalIgnoreCase);
    }
}

