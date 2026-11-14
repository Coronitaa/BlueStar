using BlueStar.Core.Models;
using BlueStar.Infrastructure.Compat;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public sealed class CompatRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-compat-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var full = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    private static readonly CompatHealth Healthy = new(UmuFound: true, UserNamespacesBlocked: false, Python3Found: true);
    private static readonly CompatHealth Blocked = new(UmuFound: true, UserNamespacesBlocked: true, Python3Found: true);

    private GameInstance Game(uint appId = 0, string? args = null) =>
        new() { Name = "Test Game", InstallPath = Path.Combine(_root, "game"), AppId = appId, LaunchArguments = args };

    private ProtonInstall Proton(string name)
    {
        Touch("steam", "steamapps", "common", name, "proton");
        Touch("steam", "steamapps", "common", name, "files", "bin", "wineserver");
        return new ProtonInstall(name, Path.Combine(_root, "steam", "steamapps", "common", name));
    }

    [Fact]
    public void DllOverrides_DetectsProxyDllsIgnoringCase_AndAppendsUserEntries()
    {
        Touch("game", "WinHttp.dll");
        Touch("game", "dinput8.DLL");

        var detected = CompatEnvironmentBuilder.DetectDllOverrides(Path.Combine(_root, "game"));

        detected.Should().BeEquivalentTo("winhttp", "dinput8");
        CompatEnvironmentBuilder.DllOverrides(detected, "d3d9=n,b;").Should().Be("winhttp,dinput8=n,b;d3d9=n,b");
        CompatEnvironmentBuilder.DllOverrides([], null).Should().BeNull();
    }

    [Fact]
    public void Plan_Umu_SetsPrefixGameIdProtonPathAndKeepsArguments()
    {
        var exe = Touch("game", "Game.exe");
        Touch("game", "winhttp.dll");
        var prefix = new CompatPrefix("/p", "/p");
        var settings = new CompatSettings { ExtraEnvironment = new Dictionary<string, string> { ["DXVK_HUD"] = "fps", ["GAMEID"] = "umu-custom" } };

        var cmd = CompatLauncher.Plan(settings, Game(480, "-windowed \"-name Bob\""), exe, prefix, "/steam", "/usr/bin/umu-run", Healthy, []);

        cmd.Strategy.Should().Be(CompatLaunchStrategy.Umu);
        cmd.FileName.Should().Be("/usr/bin/umu-run");
        cmd.Arguments.Should().Equal(exe, "-windowed", "-name Bob");
        cmd.Environment["WINEPREFIX"].Should().Be("/p");
        cmd.Environment.Should().NotContainKey("PROTONPATH", "unset lets umu pick and update UMU-Proton");
        cmd.Environment.Should().ContainKey("STORE");
        cmd.Environment["WINEDLLOVERRIDES"].Should().Be("winhttp=n,b");
        cmd.Environment["DXVK_HUD"].Should().Be("fps");
        cmd.Environment["GAMEID"].Should().Be("umu-custom", "the user's own variables win");
    }

    [Fact]
    public void Plan_FallsBackToProtonDirect_WhenUserNamespacesAreBlocked()
    {
        var exe = Touch("game", "Game.exe");
        var older = Proton("Proton 9.0");
        var newer = Proton("Proton 10.0");
        var protons = CompatRunnerLocator.FindProtons(_root, Path.Combine(_root, "steam"));

        var cmd = CompatLauncher.Plan(new CompatSettings(), Game(), exe, new CompatPrefix("/p", "/p"), "/steam", "/usr/bin/umu-run", Blocked, protons);

        protons.Select(p => p.Name).Should().Equal("Proton 10.0", "Proton 9.0");
        cmd.Strategy.Should().Be(CompatLaunchStrategy.ProtonDirect);
        cmd.FileName.Should().Be(newer.Script);
        cmd.Arguments[0].Should().Be("waitforexitandrun");
        cmd.Environment["STEAM_COMPAT_DATA_PATH"].Should().Be("/p");
        cmd.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"].Should().Be("/steam");
        cmd.Environment["SteamAppId"].Should().Be("0");
        cmd.WineServer.Should().Be(newer.WineServer);
        cmd.Notice.Should().Contain("user namespaces");
        older.Should().NotBeNull();
    }

    [Fact]
    public void Plan_ProtonDirect_WithoutAnyProton_Throws()
    {
        var exe = Touch("game", "Game.exe");
        var settings = new CompatSettings { Strategy = CompatLaunchStrategy.ProtonDirect };

        var act = () => CompatLauncher.Plan(settings, Game(), exe, new CompatPrefix("/p", "/p"), "/steam", null, Healthy, []);

        act.Should().Throw<InvalidOperationException>().WithMessage("*No Proton*");
    }

    [Fact]
    public void Plan_ThroughSteam_UsesAppIdForSteamGamesAndShortcutIdOtherwise()
    {
        var settings = new CompatSettings { Strategy = CompatLaunchStrategy.ThroughSteam };
        var steamGame = Game(1091500) with { Origin = InstanceOrigin.Steam };

        CompatLauncher.Plan(settings, steamGame, "/g/a.exe", new CompatPrefix("/p", "/p"), "/s", null, Healthy, [])
            .SteamUrl.Should().Be("steam://rungameid/1091500");

        var shortcut = CompatLauncher.Plan(settings, Game(), "/g/a.exe", new CompatPrefix("/p", "/p"), "/s", null, Healthy, []);
        var id = ulong.Parse(shortcut.SteamUrl!.Split('/').Last());
        (id & 0xFFFFFFFF).Should().Be(0x02000000UL);
        (id >> 32).Should().BeGreaterThanOrEqualTo(0x80000000UL);
    }

    [Fact]
    public void Health_ReportsAppArmorAndMissingUmu()
    {
        var blocked = CompatHealth.Check("/usr/bin/umu-run", "", p => p == CompatHealth.AppArmorUsernsSysctl ? "1\n" : null);
        blocked.CanUseUmu.Should().BeFalse();

        CompatHealth.Check(null, "", _ => null).FallbackReason.Should().Contain("not installed");
    }

    [Fact]
    public void Settings_InstanceOverrideWinsFieldByField()
    {
        var global = new CompatSettings
        {
            Strategy = CompatLaunchStrategy.Umu,
            Runner = "/proton/a",
            ExtraEnvironment = new Dictionary<string, string> { ["A"] = "1", ["B"] = "1" },
            ExtraDllOverrides = "d3d9=n,b",
        };
        var mine = new CompatSettings
        {
            Strategy = CompatLaunchStrategy.ThroughSteam,
            ExtraEnvironment = new Dictionary<string, string> { ["B"] = "2" },
            ExtraDllOverrides = "xinput1_3=n,b",
        };

        var merged = global.WithOverride(mine);

        merged.Strategy.Should().Be(CompatLaunchStrategy.Umu);
        merged.Runner.Should().Be("/proton/a");
        merged.ExtraEnvironment.Should().Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
        merged.ExtraDllOverrides.Should().Be("d3d9=n,b;xinput1_3=n,b");
        global.WithOverride(null).Should().BeSameAs(global);
    }

    [Fact]
    public void Prefix_UsesSteamCompatDataForSteamGames_ElseBlueStarFolder()
    {
        var steam = Path.Combine(_root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "compatdata", "480"));
        var steamGame = Game(480) with { Origin = InstanceOrigin.Steam };

        var p = CompatPrefix.For(steamGame, _root, steam);
        p.CompatDataPath.Should().Be(Path.Combine(steam, "steamapps", "compatdata", "480"));
        p.WinePrefix.Should().Be(Path.Combine(p.CompatDataPath, "pfx"));

        var own = CompatPrefix.For(Game(), _root, steam);
        own.IsFlat.Should().BeTrue();
        own.WinePrefix.Should().Contain(Path.Combine("BlueStar", "prefixes"));
    }

    [LinuxFact]
    public void Prefix_EnsureCreated_LinksPfxToItself()
    {
        var prefix = new CompatPrefix(Path.Combine(_root, "pfxdir"), Path.Combine(_root, "pfxdir"));

        prefix.EnsureCreated();
        prefix.EnsureCreated();

        new DirectoryInfo(Path.Combine(_root, "pfxdir", "pfx")).LinkTarget.Should().Be(".");
    }

    // A root-owned rwxr--r-- umu-run (seen on a WSL install) must not count as installed: launching it fails with
    // "Permission denied" instead of falling back to Proton directly.
    [LinuxFact]
    public void FindUmuRun_IgnoresACopyThisUserCannotExecute()
    {
        var bin = Path.Combine(_root, "bin");
        var umu = Touch("bin", "umu-run");
        File.SetUnixFileMode(umu, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        CompatRunnerLocator.FindUmuRun(_root, bin).Should().BeNull();

        File.SetUnixFileMode(umu, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CompatRunnerLocator.FindUmuRun(_root, bin).Should().Be(umu);
    }

    [Fact]
    public void SplitArguments_HandlesQuotes()
    {
        CompatLauncher.SplitArguments("  -a  \"b c\" d ").Should().Equal("-a", "b c", "d");
        CompatLauncher.SplitArguments(null).Should().BeEmpty();
    }

    [Fact]
    public void Umu_UsesUmuDefaultWithoutAppId_AndPassesGeProtonCodename()
    {
        var settings = new CompatSettings { Runner = CompatSettings.GeProtonLatest };

        var env = CompatEnvironmentBuilder.ForUmu(settings, Game(), new CompatPrefix("/p", "/p"), null);

        env["GAMEID"].Should().Be("umu-default");
        env["PROTONPATH"].Should().Be("GE-Proton");
    }
}
