using System.Diagnostics;
using System.Text;

namespace BlueStar.Infrastructure.Platform;

/// <summary>freedesktop.org integration: .desktop entries and URL scheme handlers.</summary>
public static class LinuxDesktopIntegration
{
    public const string NxmDesktopFile = "bluestar-nxm.desktop";
    public static readonly string[] NxmSchemes = ["nxm", "vortex", "nxm-protocol"];

    /// <summary>
    /// What a .desktop file or Steam shortcut must run. Inside an AppImage, ProcessPath points into a temporary
    /// /tmp/.mount_* folder that is gone after exit; $APPIMAGE is the real file.
    /// </summary>
    public static string LauncherPath() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
            ? appImage
            : Environment.ProcessPath ?? "bluestar";

    /// <summary>Quotes a path for a .desktop Exec line (spec: double quotes, escape " ` $ \).</summary>
    public static string QuoteExec(string path)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    public static string BuildNxmDesktopEntry(string launcher) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=BlueStar (Nexus Mods links)
        Exec={QuoteExec(launcher)} %u
        NoDisplay=true
        Terminal=false
        MimeType={string.Join(';', NxmSchemes.Select(s => "x-scheme-handler/" + s))};

        """;

    /// <summary>Writes ~/.local/share/applications/bluestar-nxm.desktop and makes it the default for nxm:// links.</summary>
    public static void RegisterNxmHandler(string home)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        var apps = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, NxmDesktopFile), BuildNxmDesktopEntry(LauncherPath()));

        foreach (var scheme in NxmSchemes)
        {
            using var p = Process.Start(new ProcessStartInfo("xdg-mime", ["default", NxmDesktopFile, "x-scheme-handler/" + scheme]) { UseShellExecute = false });
            p?.WaitForExit(5000);
        }
    }
}
