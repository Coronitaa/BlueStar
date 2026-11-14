namespace BlueStar.Core.Helpers;

/// <summary>
/// Finds files and folders inside a game tree ignoring case. Windows games ship names like
/// <c>Steam_API64.dll</c> or <c>Binaries/Win64</c> in any casing, which only matters on Linux filesystems.
/// </summary>
public static class GameFileLocator
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>Full path of <paramref name="relativePath"/> under <paramref name="root"/>, or null when it does not exist.</summary>
    public static string? FindFile(string root, string relativePath) => Resolve(root, relativePath, wantFile: true);

    /// <summary>Full path of the folder <paramref name="relativePath"/> under <paramref name="root"/>, or null when it does not exist.</summary>
    public static string? FindDirectory(string root, string relativePath) => Resolve(root, relativePath, wantFile: false);

    private static string? Resolve(string root, string relativePath, bool wantFile)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var segments = relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return wantFile ? null : root;

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var next = MatchEntry(current, segments[i], isLast && wantFile);
            if (next == null)
                return null;
            current = next;
        }

        return current;
    }

    private static string? MatchEntry(string dir, string name, bool file)
    {
        var exact = Path.Combine(dir, name);
        if (file ? File.Exists(exact) : Directory.Exists(exact))
            return exact;

        try
        {
            var entries = file ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir);
            return entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
