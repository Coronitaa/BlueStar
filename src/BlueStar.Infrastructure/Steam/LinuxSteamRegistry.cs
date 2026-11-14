using System.Text.RegularExpressions;

namespace BlueStar.Infrastructure.Steam;

/// <summary>What Linux Steam writes to <c>~/.steam/registry.vdf</c>: the same values as HKCU\Software\Valve\Steam on Windows.</summary>
public sealed record LinuxSteamRegistryInfo(int? Pid, uint? ActiveUser, string? AutoLoginUser);

/// <summary>Reads <c>~/.steam/registry.vdf</c>, the Linux stand-in for the Steam registry keys.</summary>
public static class LinuxSteamRegistry
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "registry.vdf");

    public static LinuxSteamRegistryInfo? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null; // Steam rewrites the file while running; the next poll reads it
        }
    }

    public static LinuxSteamRegistryInfo Parse(string content)
    {
        // ActiveProcess has no nested blocks, so the first closing brace ends it.
        var active = Regex.Match(content, "\"ActiveProcess\"\\s*\\{([^}]*)\\}", RegexOptions.IgnoreCase);
        var activeBlock = active.Success ? active.Groups[1].Value : string.Empty;

        int? pid = int.TryParse(Value(activeBlock, "pid"), out var p) && p > 0 ? p : null;
        uint? user = uint.TryParse(Value(activeBlock, "ActiveUser"), out var u) && u > 0 ? u : null;
        var autoLogin = Value(content, "AutoLoginUser");

        return new LinuxSteamRegistryInfo(pid, user, string.IsNullOrWhiteSpace(autoLogin) ? null : autoLogin);
    }

    private static string? Value(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
