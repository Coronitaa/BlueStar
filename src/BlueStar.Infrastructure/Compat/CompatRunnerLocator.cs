using System.Text.RegularExpressions;

namespace BlueStar.Infrastructure.Compat;

/// <summary>A Proton build on disk: its folder holds the <c>proton</c> script.</summary>
public sealed record ProtonInstall(string Name, string Directory)
{
    public string Script => Path.Combine(Directory, "proton");

    /// <summary>Newer Proton keeps Wine in files/, older (≤ 5.0) in dist/.</summary>
    public string? WineServer =>
        new[] { "files", "dist" }.Select(d => Path.Combine(Directory, d, "bin", "wineserver")).FirstOrDefault(File.Exists);
}

/// <summary>Finds umu-run and every Proton build Steam or the user installed.</summary>
public static class CompatRunnerLocator
{
    public static string DataHome(string home) =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");

    /// <summary>
    /// umu-run on PATH, else the copy BlueStar keeps under $XDG_DATA_HOME/BlueStar/runtimes/umu. Only a copy this user
    /// can execute counts: a root-owned rwxr--r-- install would fail with "Permission denied" instead of falling back.
    /// </summary>
    public static string? FindUmuRun(string home, string? pathVariable)
    {
        foreach (var dir in (pathVariable ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "umu-run");
            if (IsExecutable(candidate))
                return candidate;
        }

        var bundled = Path.Combine(DataHome(home), "BlueStar", "runtimes", "umu", "umu-run");
        return IsExecutable(bundled) ? bundled : null;
    }

    /// <summary>The file exists and the current user may run it (access X_OK on Unix).</summary>
    public static bool IsExecutable(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows() || access(path, XOk) == 0);

    private const int XOk = 1;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    /// <summary>
    /// Proton builds in every Steam library (steamapps/common/Proton*) and in the custom tool folders
    /// (compatibilitytools.d of Steam and of umu), newest name first.
    /// </summary>
    public static IReadOnlyList<ProtonInstall> FindProtons(string home, string? steamRoot)
    {
        var roots = new List<string>();
        if (steamRoot != null)
        {
            roots.Add(Path.Combine(steamRoot, "compatibilitytools.d"));
            roots.AddRange(SteamLibraries(steamRoot).Select(lib => Path.Combine(lib, "steamapps", "common")));
        }
        roots.Add(Path.Combine(DataHome(home), "umu", "compatibilitytools"));

        return roots
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(d => File.Exists(Path.Combine(d, "proton")))
            .Select(d => new ProtonInstall(Path.GetFileName(d), d))
            .DistinctBy(p => p.Directory)
            .OrderByDescending(p => p.Name, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>Library folders from steamapps/libraryfolders.vdf, the Steam root always included.</summary>
    public static IReadOnlyList<string> SteamLibraries(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }

        return libs.Where(Directory.Exists).Distinct().ToList();
    }

    /// <summary>"Proton 10.0" after "Proton 9.0", "GE-Proton10-3" after "GE-Proton9-27".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Regex.Split(x ?? "", "([0-9]+)");
            var b = Regex.Split(y ?? "", "([0-9]+)");
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = long.TryParse(a[i], out var na) && long.TryParse(b[i], out var nb)
                    ? na.CompareTo(nb)
                    : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
