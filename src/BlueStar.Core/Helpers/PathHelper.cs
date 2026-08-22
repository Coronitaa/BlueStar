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
}
