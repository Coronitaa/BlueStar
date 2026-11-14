using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Platform;
using BlueStar.Infrastructure.Steam;
using FluentAssertions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

/// <summary>Services the Avalonia head uses instead of WPF ViewModel code.</summary>
public sealed class LinuxHeadServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-head-").FullName;
    private readonly Mock<IInstanceManager> _instances = new();
    private readonly Mock<IEngineDetector> _engines = new();
    private readonly List<GameInstance> _saved = [];

    public LinuxHeadServicesTests()
    {
        _instances.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _saved.ToList());
        _instances.Setup(m => m.CreateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance i, CancellationToken _) => { _saved.Add(i); return i; });
        _instances.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance i, CancellationToken _) => i);
        _engines.Setup(e => e.DetectEngineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInfo { Id = "unity", Name = "Unity", Type = EngineType.Unity });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string GameFolder(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, "Game.exe");
        File.WriteAllText(exe, "MZ");
        _engines.Setup(e => e.FindPrimaryExecutable(dir, It.IsAny<string?>())).Returns(exe);
        return dir;
    }

    [Fact]
    public async Task ImportFolder_CreatesReadyInstance_AndRejectsTheSameFolderTwice()
    {
        var folder = GameFolder("My Game");
        var import = new GameImportService(_instances.Object, _engines.Object);

        var created = await import.ImportFolderAsync(folder);

        created.Name.Should().Be("My Game");
        created.Origin.Should().Be(InstanceOrigin.ImportedFolder);
        created.Status.Should().Be(InstanceStatus.Ready);
        created.Engine!.Type.Should().Be(EngineType.Unity);
        await import.Invoking(i => i.ImportFolderAsync(folder + Path.DirectorySeparatorChar))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*My Game*");
    }

    [Fact]
    public async Task ImportSteamGame_KeepsAppIdAndOrigin_AndSurvivesMetadataFailure()
    {
        var folder = GameFolder("Spacewar Clone");
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetMetadataAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("offline"));
        var import = new GameImportService(_instances.Object, _engines.Object, metadata.Object);

        var created = await import.ImportSteamGameAsync(new InstalledSteamGame(1091500, "Cyberpunk", "c", folder, 0));

        created.AppId.Should().Be(1091500);
        created.Origin.Should().Be(InstanceOrigin.Steam);
        created.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ReFixInstall_SavesOptionAndVersion_UninstallClearsThem()
    {
        var instance = new GameInstance { Name = "G", InstallPath = _root };
        var lifecycle = new Mock<IEmulatorLifecycleService>();
        lifecycle.Setup(l => l.DeployOrUpdateEmulatorWithDlcPreservationAsync(instance, "refix_goldberg", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var updates = new Mock<IReFixUpdateService>();
        updates.Setup(u => u.GetCurrentInstalledVersion()).Returns("2.5.0");
        var refix = new ReFixInstanceService(lifecycle.Object, _instances.Object, updates.Object);

        var installed = await refix.InstallAsync(instance, "refix_goldberg");
        installed!.EmulatorEnabled.Should().BeTrue();
        installed.EmulatorId.Should().Be("refix_goldberg");
        installed.InstalledEmulatorVersion.Should().Be("2.5.0");

        var removed = await refix.UninstallAsync(installed);
        removed.EmulatorEnabled.Should().BeFalse();
        removed.EmulatorId.Should().BeNull();
    }

    [Fact]
    public async Task ReFixInstall_ReturnsNullWhenDeployFails()
    {
        var lifecycle = new Mock<IEmulatorLifecycleService>();
        var refix = new ReFixInstanceService(lifecycle.Object, _instances.Object);

        (await refix.InstallAsync(new GameInstance { Name = "G", InstallPath = _root }, "refix_valve")).Should().BeNull();
        _instances.Verify(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("2.39", "2.39")]
    [InlineData("2.31-0ubuntu9", "2.31")]
    [InlineData("2", "2.0")]
    [InlineData("", null)]
    public void ParseGlibc(string raw, string? expected) =>
        LinuxRequirements.ParseGlibc(raw).Should().Be(expected == null ? null : Version.Parse(expected));

    [Fact]
    public void ParseEnvironment_SkipsCommentsAndReportsTheBadLine()
    {
        CompatSettings.ParseEnvironment("DXVK_HUD=fps\n# note\n\n A = b=c ", out var bad)
            .Should().Equal(new Dictionary<string, string> { ["DXVK_HUD"] = "fps", ["A"] = "b=c" });
        bad.Should().BeNull();

        CompatSettings.ParseEnvironment("OK=1\nnot a pair", out bad);
        bad.Should().Be("not a pair");
    }
}
