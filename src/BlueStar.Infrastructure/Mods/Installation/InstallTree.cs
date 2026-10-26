using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BlueStar.Infrastructure.Mods.Installation;

/// <summary>
/// Shallow index of a game's folder tree (relative paths), used to anchor mod archives
/// against folders that already exist.
/// </summary>
internal sealed class InstallTree
{
    private const int MaxDepth = 5;

    private readonly List<string> _directories;

    public InstallTree(string root)
    {
        Root = Path.GetFullPath(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        _directories = Directory.EnumerateDirectories(Root, "*", options)
            .Select(d => Path.GetRelativePath(Root, d))
            .OrderBy(d => Depth(d))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Root { get; }

    /// <summary>
    /// Finds the shallowest existing folder named <paramref name="folderName"/>. When several share that depth,
    /// prefers one that already contains <paramref name="childHint"/>. Returns a path relative to the root.
    /// </summary>
    public string? FindAnchor(string folderName, string? childHint)
    {
        var matches = _directories
            .Where(d => Path.GetFileName(d).Equals(folderName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;

        var shallowest = Depth(matches[0]);
        var candidates = matches.Where(m => Depth(m) == shallowest).ToList();
        if (candidates.Count > 1 && childHint != null)
        {
            var withChild = candidates.FirstOrDefault(c => HasDirectory(Path.Combine(c, childHint)));
            if (withChild != null) return withChild;
        }

        return candidates[0];
    }

    /// <summary>Every folder named <paramref name="folderName"/>, shallowest first, relative to the root.</summary>
    public IReadOnlyList<string> FindAll(string folderName) =>
        _directories.Where(d => Path.GetFileName(d).Equals(folderName, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool HasDirectory(string relativePath) =>
        _directories.Any(d => d.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The on-disk spelling of a top-level folder, e.g. "Mods" for a lookup of "mods".</summary>
    public string ActualName(string relativePath) =>
        _directories.FirstOrDefault(d => d.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) ?? relativePath;

    /// <summary>Game Content/Paks folder, skipping the engine's own Engine/... paks.</summary>
    public string? FindContentPaks() =>
        _directories.FirstOrDefault(d =>
            Path.GetFileName(d).Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(d)), "Content", StringComparison.OrdinalIgnoreCase) &&
            !d.StartsWith("Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public bool HasUnityDataFolder() =>
        _directories.Any(d => Depth(d) == 2 &&
                              Path.GetFileName(d).Equals("Managed", StringComparison.OrdinalIgnoreCase) &&
                              d.Split(Path.DirectorySeparatorChar)[0].EndsWith("_Data", StringComparison.OrdinalIgnoreCase));

    /// <summary>Shallowest folder (root included) that contains <paramref name="fileName"/>, relative to the root.</summary>
    public string? FindDirectoryContainingFile(string fileName)
    {
        if (File.Exists(Path.Combine(Root, fileName))) return string.Empty;
        return _directories.FirstOrDefault(d => File.Exists(Path.Combine(Root, d, fileName)));
    }

    private static int Depth(string relativePath) =>
        relativePath.Count(c => c == Path.DirectorySeparatorChar) + 1;
}
