namespace BlueStar.Infrastructure.Compat;

/// <summary>Whether umu can run here. umu starts games in a bubblewrap container, which needs unprivileged user namespaces.</summary>
public sealed record CompatHealth(bool UmuFound, bool UserNamespacesBlocked, bool Python3Found)
{
    public const string AppArmorUsernsSysctl = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";
    public const string UsernsCloneSysctl = "/proc/sys/kernel/unprivileged_userns_clone";

    public bool CanUseUmu => UmuFound && Python3Found && !UserNamespacesBlocked;

    /// <summary>Why BlueStar fell back to ProtonDirect, for the user; null when umu works.</summary>
    public string? FallbackReason =>
        !UmuFound ? "umu-launcher is not installed"
        : !Python3Found ? "python3 is missing (umu-launcher needs it)"
        : UserNamespacesBlocked ? "the system blocks unprivileged user namespaces (AppArmor kernel.apparmor_restrict_unprivileged_userns=1 or kernel.unprivileged_userns_clone=0), which umu's container needs"
        : null;

    /// <param name="readFile">Reads a /proc file; returns null when it does not exist.</param>
    public static CompatHealth Check(string? umuRun, string? pathVariable, Func<string, string?> readFile)
    {
        var restricted = readFile(AppArmorUsernsSysctl)?.Trim() == "1"
                         || readFile(UsernsCloneSysctl)?.Trim() == "0";
        var python = (pathVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, "python3")));
        return new CompatHealth(umuRun != null, restricted, python);
    }

    public static string? ReadProcFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
