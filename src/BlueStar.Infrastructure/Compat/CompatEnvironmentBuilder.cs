using BlueStar.Core.Helpers;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Compat;

/// <summary>Builds the environment for a Wine/Proton launch. Pure apart from reading the game folder.</summary>
public static class CompatEnvironmentBuilder
{
    // Proxy DLLs mods and ReFix drop next to the exe: Wine must load the game's copy before its builtin.
    private static readonly (string Dll, string Name)[] ProxyDlls =
    [
        ("winhttp.dll", "winhttp"),   // BepInEx 5 doorstop
        ("winmm.dll", "winmm"),       // ReFix Online loader
        ("version.dll", "version"),   // BepInEx 6, many ASI / UE4SS loaders
        ("dinput8.dll", "dinput8"),   // Ultimate ASI Loader, UE4SS
    ];

    /// <summary>The proxy DLLs present in <paramref name="exeDir"/> that need a native-first override.</summary>
    public static IReadOnlyList<string> DetectDllOverrides(string exeDir) =>
        ProxyDlls.Where(p => GameFileLocator.FindFile(exeDir, p.Dll) != null).Select(p => p.Name).ToList();

    /// <summary>WINEDLLOVERRIDES value: detected proxies as <c>n,b</c>, then the user's own entries.</summary>
    public static string? DllOverrides(IEnumerable<string> detected, string? extra)
    {
        var parts = new List<string>();
        var names = detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0)
            parts.Add(string.Join(',', names) + "=n,b");
        if (!string.IsNullOrWhiteSpace(extra))
            parts.Add(extra.Trim().Trim(';'));
        return parts.Count == 0 ? null : string.Join(';', parts);
    }

    /// <summary>
    /// umu-run: WINEPREFIX is the prefix, GAMEID picks protonfixes (umu-&lt;Steam AppId&gt;; umu's documented
    /// default umu-default applies none), PROTONPATH a Proton folder or a codename such as GE-Proton. Left unset,
    /// umu uses and updates UMU-Proton itself.
    /// </summary>
    public static Dictionary<string, string> ForUmu(CompatSettings settings, GameInstance instance, CompatPrefix prefix, string? dllOverrides)
    {
        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = prefix.WinePrefix,
            ["GAMEID"] = instance.AppId > 0 ? $"umu-{instance.AppId}" : "umu-default",
            ["STORE"] = "none",
        };
        if (!string.IsNullOrWhiteSpace(settings.Runner) && settings.Runner != CompatSettings.UmuProtonAuto)
            env["PROTONPATH"] = settings.Runner;
        return Finish(env, settings, dllOverrides);
    }

    /// <summary>Proton's own script: it needs the compat data folder, the Steam client folder and the AppId.</summary>
    public static Dictionary<string, string> ForProton(CompatSettings settings, GameInstance instance, CompatPrefix prefix, string steamRoot, string? dllOverrides)
    {
        var appId = instance.AppId > 0 ? instance.AppId.ToString() : "0";
        var env = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = prefix.CompatDataPath,
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot,
            ["WINEPREFIX"] = prefix.WinePrefix,
            ["SteamAppId"] = appId,
            ["SteamGameId"] = appId,
        };
        return Finish(env, settings, dllOverrides);
    }

    private static Dictionary<string, string> Finish(Dictionary<string, string> env, CompatSettings settings, string? dllOverrides)
    {
        if (dllOverrides != null)
            env["WINEDLLOVERRIDES"] = dllOverrides;
        foreach (var (key, value) in settings.ExtraEnvironment)
            env[key] = value; // the user's own values win, even over BlueStar's
        return env;
    }
}
