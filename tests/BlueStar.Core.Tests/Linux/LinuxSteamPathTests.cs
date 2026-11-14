using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests.Linux;

public sealed class LinuxSteamPathTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("bs-home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static string Norm(string? p) => Path.GetFullPath(p!);

    [Fact]
    public void FindLinuxSteamPath_ReturnsNull_WhenNoSteam()
    {
        ShortcutHelper.FindLinuxSteamPath(_home).Should().BeNull();
    }

    [Fact]
    public void FindLinuxSteamPath_FindsNativeInstall()
    {
        var steam = Path.Combine(_home, ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));

        Norm(ShortcutHelper.FindLinuxSteamPath(_home)).Should().Be(Norm(steam));
    }

    [Fact]
    public void FindLinuxSteamPath_PrefersDotSteamOverFlatpak()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps"));
        var dotSteam = Path.Combine(_home, ".steam", "steam");
        Directory.CreateDirectory(dotSteam);
        File.WriteAllText(Path.Combine(dotSteam, "steam.sh"), "");

        Norm(ShortcutHelper.FindLinuxSteamPath(_home)).Should().Be(Norm(dotSteam));
    }

    [Fact]
    public void FindLinuxSteamPath_FindsFlatpak()
    {
        var flatpak = Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(flatpak, "steamapps"));

        Norm(ShortcutHelper.FindLinuxSteamPath(_home)).Should().Be(Norm(flatpak));
    }

    [Fact]
    public void FindLinuxSteamPath_IgnoresSnap()
    {
        Directory.CreateDirectory(Path.Combine(_home, "snap", "steam", "common", ".local", "share", "Steam", "steamapps"));

        ShortcutHelper.FindLinuxSteamPath(_home).Should().BeNull();
    }
}
