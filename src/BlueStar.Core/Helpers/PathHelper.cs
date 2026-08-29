using System;
using System.IO;
using System.Linq;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Helper methods for path manipulation and game folder creation.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Sanitizes a string for use as a folder name by replacing invalid path characters.
    /// </summary>
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Game";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Game" : sanitized;
    }

    /// <summary>
    /// Ensures that <paramref name="selectedFolder"/> includes a subfolder with <paramref name="gameName"/>.
    /// If <paramref name="selectedFolder"/> already ends with <paramref name="gameName"/>, returns it as-is.
    /// </summary>
    public static string EnsureGameSubfolder(string selectedFolder, string gameName)
    {
        if (string.IsNullOrWhiteSpace(selectedFolder))
            return string.Empty;

        var cleanFolder = selectedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderLeafName = Path.GetFileName(cleanFolder);
        var safeGameName = SanitizeFolderName(gameName);

        if (string.Equals(folderLeafName, safeGameName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderLeafName, gameName, StringComparison.OrdinalIgnoreCase))
        {
            return cleanFolder;
        }

        return Path.Combine(cleanFolder, safeGameName);
    }

    /// <summary>
    /// Generates a unique instance name to prevent naming collisions.
    /// If an instance named "Game" already exists, returns "Game (2)", "Game (3)", etc.
    /// </summary>
    public static string GenerateUniqueInstanceName(IEnumerable<string> existingNames, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Game";
        var existingSet = new HashSet<string>(
            (existingNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        if (!existingSet.Contains(baseName))
            return baseName;

        var match = System.Text.RegularExpressions.Regex.Match(baseName.Trim(), @"^(.*?)\s*\(\d+\)$");
        var rootName = match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : baseName.Trim();

        int index = 2;
        while (existingSet.Contains($"{rootName} ({index})") || existingSet.Contains($"{baseName} ({index})"))
        {
            index++;
        }
        return $"{rootName} ({index})";
    }

    /// <summary>
    /// Generates a unique installation path to prevent folder collisions with existing instances or directories.
    /// </summary>
    public static string GenerateUniqueInstallPath(string baseDirectory, string gameName, IEnumerable<string>? existingPaths = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
        }

        var existingSet = new HashSet<string>(
            (existingPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase);

        var sanitized = SanitizeFolderName(gameName);
        var targetPath = EnsureGameSubfolder(baseDirectory, sanitized);
        var cleanTarget = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!existingSet.Contains(cleanTarget) && !Directory.Exists(cleanTarget))
            return cleanTarget;

        int index = 2;
        while (true)
        {
            var candidateName = $"{sanitized} ({index})";
            var candidatePath = Path.Combine(baseDirectory, candidateName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!existingSet.Contains(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
            index++;
        }
    }
}
