using System.Runtime.InteropServices;
using BlueStar.Core.Helpers;
using BlueStar.Infrastructure.Compat;

namespace BlueStar.Infrastructure.Platform;

/// <summary>One line of the Linux requirements page. <see cref="Required"/> false means "only some features need it".</summary>
public sealed record RequirementResult(string Name, bool Ok, string Detail, bool Required);

/// <summary>What BlueStar needs on Linux to start Windows games, checked once at startup and on demand.</summary>
public static class LinuxRequirements
{
    public static readonly Version MinGlibc = new(2, 31);

    // i386 loader: SteamCMD (Workshop downloads) is a 32-bit program.
    private static readonly string[] Loader32 = ["/lib/ld-linux.so.2", "/usr/lib/ld-linux.so.2", "/usr/lib32/ld-linux.so.2"];

    public static IReadOnlyList<RequirementResult> Check()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Environment.GetEnvironmentVariable("PATH");
        var steam = ShortcutHelper.GetSteamPath();
        var umu = CompatRunnerLocator.FindUmuRun(home, path);
        var health = CompatHealth.Check(umu, path, CompatHealth.ReadProcFile);
        var protons = CompatRunnerLocator.FindProtons(home, steam);
        var glibc = GlibcVersion();

        return
        [
            new("glibc ≥ 2.31", glibc != null && glibc >= MinGlibc, glibc?.ToString() ?? "unknown", true),
            new("Steam", steam != null, steam ?? "not found (~/.steam, ~/.local/share/Steam or Flatpak)", false),
            new("umu-launcher", umu != null, umu ?? "not installed: BlueStar uses Proton directly", false),
            new("python3", health.Python3Found, health.Python3Found ? "found" : "missing (umu-launcher needs it)", false),
            new("User namespaces", !health.UserNamespacesBlocked,
                health.UserNamespacesBlocked ? "blocked by the system: umu cannot run, BlueStar uses Proton directly" : "allowed", false),
            new("Proton", protons.Count > 0 || health.CanUseUmu,
                protons.Count > 0 ? string.Join(", ", protons.Select(p => p.Name)) : health.CanUseUmu ? "umu downloads UMU-Proton itself" : "none found: install Proton from Steam", true),
            new("Vulkan", NativeLibrary.TryLoad("libvulkan.so.1", out _), "libvulkan.so.1 (DXVK/VKD3D need it)", true),
            new("32-bit glibc", Loader32.Any(File.Exists), "for SteamCMD (Workshop mods): lib32gcc-s1 / glibc.i686", false),
        ];
    }

    /// <summary>"2.39" or "2.39-0ubuntu8" → 2.39.</summary>
    public static Version? ParseGlibc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var numeric = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(numeric.Contains('.') ? numeric : numeric + ".0", out var v) ? v : null;
    }

    private static Version? GlibcVersion()
    {
        try { return ParseGlibc(Marshal.PtrToStringAnsi(gnu_get_libc_version())); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; } // musl
    }

    [DllImport("libc")]
    private static extern IntPtr gnu_get_libc_version();
}
