using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Compat;

/// <summary>
/// Where a game's Wine prefix lives. <see cref="WinePrefix"/> is the folder holding drive_c;
/// <see cref="CompatDataPath"/> is what Proton gets as STEAM_COMPAT_DATA_PATH (it uses <c>&lt;that&gt;/pfx</c>).
/// </summary>
public sealed record CompatPrefix(string WinePrefix, string CompatDataPath)
{
    /// <summary>
    /// Steam-owned games keep Steam's own compatdata/&lt;appid&gt; so saves stay with the user's Steam copy;
    /// everything else gets $XDG_DATA_HOME/BlueStar/prefixes/&lt;instanceId&gt;.
    /// </summary>
    public static CompatPrefix For(GameInstance instance, string home, string? steamRoot)
    {
        if (!string.IsNullOrWhiteSpace(instance.CompatPrefixPath))
            return Flat(instance.CompatPrefixPath);

        if (instance.Origin == InstanceOrigin.Steam && instance.AppId > 0 && steamRoot != null)
        {
            var compatData = CompatRunnerLocator.SteamLibraries(steamRoot)
                .Select(lib => Path.Combine(lib, "steamapps", "compatdata", instance.AppId.ToString()))
                .FirstOrDefault(Directory.Exists);
            if (compatData != null)
                return new CompatPrefix(Path.Combine(compatData, "pfx"), compatData);
        }

        return Flat(Path.Combine(CompatRunnerLocator.DataHome(home), "BlueStar", "prefixes", instance.Id.ToString("N")));
    }

    /// <summary>BlueStar's own prefixes are flat: umu and Proton both see the same drive_c.</summary>
    private static CompatPrefix Flat(string dir) => new(dir, dir);

    public bool IsFlat => WinePrefix == CompatDataPath;

    /// <summary>
    /// Creates a flat prefix folder with <c>pfx -&gt; .</c>, the layout umu writes, so ProtonDirect
    /// (which always uses STEAM_COMPAT_DATA_PATH/pfx) lands in the same drive_c when the strategy changes.
    /// </summary>
    public void EnsureCreated()
    {
        if (!IsFlat)
            return;

        Directory.CreateDirectory(WinePrefix);
        var pfx = Path.Combine(WinePrefix, "pfx");
        if (!Directory.Exists(pfx) && !File.Exists(pfx))
            Directory.CreateSymbolicLink(pfx, ".");
    }
}
