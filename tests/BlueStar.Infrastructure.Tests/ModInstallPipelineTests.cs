using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.Installation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ModInstallPipelineTests : IDisposable
{
    private readonly ModInstallTestFixture _fx = new();
    private readonly JsonModLedgerStore _store;
    private readonly ModInstallPipeline _pipeline;

    public ModInstallPipelineTests()
    {
        _store = new JsonModLedgerStore(NullLogger<JsonModLedgerStore>.Instance, _fx.DataDir);
        _pipeline = new ModInstallPipeline(_store, new ModPlacementResolver(), NullLogger<ModInstallPipeline>.Instance, _fx.StagingDir);
    }

    public void Dispose() => _fx.Dispose();

    private static ModPackage Package(string path, string remoteId = "cool", string? version = "1.0") => new()
    {
        Path = path,
        Name = "Cool Mod",
        Source = new ModSourceRef { Provider = "thunderstore", RemoteId = remoteId, Version = version }
    };

    [Fact]
    public async Task InstallAsync_CopiesFiles_AndRecordsThemInLedger()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        var pkg = _fx.Package("v1", ("BepInEx/plugins/Cool.dll", "v1"));

        // Act
        var result = await _pipeline.InstallAsync(_fx.Instance(), Package(pkg));

        // Assert
        result.Status.Should().Be(ModInstallStatus.Installed);
        _fx.ReadGame("BepInEx/plugins/Cool.dll").Should().Be("v1");
        var ledger = await _store.LoadAsync(_fx.Instance().Id);
        var record = ledger.Find("thunderstore:cool");
        record.Should().NotBeNull();
        record!.Files.Should().ContainSingle().Which.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task InstallAsync_FromZipArchive_ExtractsAndInstalls()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        var folder = _fx.Package("zipsrc", ("BepInEx/plugins/Cool.dll", "zipped"));
        var zip = Path.Combine(_fx.PackagesDir, "cool.zip");
        ZipFile.CreateFromDirectory(folder, zip);

        // Act
        var result = await _pipeline.InstallAsync(_fx.Instance(), Package(zip));

        // Assert
        result.Succeeded.Should().BeTrue();
        _fx.ReadGame("BepInEx/plugins/Cool.dll").Should().Be("zipped");
        var stagingLeftovers = Directory.Exists(_fx.StagingDir) && Directory.EnumerateFileSystemEntries(_fx.StagingDir).Any();
        stagingLeftovers.Should().BeFalse("the staging folder is cleaned up");
    }

    [Fact]
    public async Task InstallAsync_LowConfidence_AsksForConfirmation_WithoutTouchingGame()
    {
        // Arrange
        var pkg = _fx.Package("unknown", ("data.bin", "x"));

        // Act
        var result = await _pipeline.InstallAsync(_fx.Instance(), Package(pkg));

        // Assert
        result.Status.Should().Be(ModInstallStatus.NeedsConfirmation);
        result.Plan!.Confidence.Should().Be(PlacementConfidence.Low);
        Directory.EnumerateFileSystemEntries(_fx.GameDir).Should().BeEmpty();

        // Act again, confirmed
        var confirmed = await _pipeline.InstallAsync(_fx.Instance(), Package(pkg), new ModInstallOptions { AllowLowConfidence = true });

        // Assert
        confirmed.Succeeded.Should().BeTrue();
        _fx.GameHas("mods/Cool Mod/data.bin").Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_RestoresGameFileTheModReplaced_AndRemovesCreatedFolders()
    {
        // Arrange
        _fx.GameFile("Data/Skyrim.esm", "original-master");
        _fx.GameFile("Data/textures/sky.dds", "original-texture");
        var pkg = _fx.Package("tex", ("textures/sky.dds", "modded"), ("meshes/new.nif", "mesh"), ("Cool.esp", "plugin"));
        await _pipeline.InstallAsync(_fx.Instance(), Package(pkg));
        _fx.ReadGame("Data/textures/sky.dds").Should().Be("modded");

        // Act
        var removed = await _pipeline.UninstallAsync(_fx.Instance(), "thunderstore:cool");

        // Assert
        removed.Should().BeTrue();
        _fx.ReadGame("Data/textures/sky.dds").Should().Be("original-texture");
        _fx.GameHas("Data/Cool.esp").Should().BeFalse();
        Directory.Exists(Path.Combine(_fx.GameDir, "Data", "meshes")).Should().BeFalse();
        (await _store.LoadAsync(_fx.Instance().Id)).Mods.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_SameSourceAgain_UpdatesFiles_AndRemovesStaleOnes()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        await _pipeline.InstallAsync(_fx.Instance(), Package(_fx.Package("v1",
            ("BepInEx/plugins/Cool.dll", "v1"), ("BepInEx/plugins/Old.dll", "old"))));

        // Act
        var result = await _pipeline.InstallAsync(_fx.Instance(),
            Package(_fx.Package("v2", ("BepInEx/plugins/Cool.dll", "v2")), version: "2.0"));

        // Assert
        result.Succeeded.Should().BeTrue();
        _fx.ReadGame("BepInEx/plugins/Cool.dll").Should().Be("v2");
        _fx.GameHas("BepInEx/plugins/Old.dll").Should().BeFalse();
        var record = (await _store.LoadAsync(_fx.Instance().Id)).Find("thunderstore:cool")!;
        record.Version.Should().Be("2.0");
        record.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task Install_FileOwnedByAnotherMod_ReportsConflict()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        await _pipeline.InstallAsync(_fx.Instance(), Package(_fx.Package("a", ("BepInEx/plugins/Shared.dll", "a")), remoteId: "a"));

        // Act
        var result = await _pipeline.InstallAsync(_fx.Instance(),
            Package(_fx.Package("b", ("BepInEx/plugins/Shared.dll", "b")), remoteId: "b"));

        // Assert
        result.Status.Should().Be(ModInstallStatus.Conflict);
        result.Conflicts.Should().ContainSingle().Which.OwnerModId.Should().Be("thunderstore:a");
        _fx.ReadGame("BepInEx/plugins/Shared.dll").Should().Be("a");
    }

    [Fact]
    public async Task Disable_ParksFilesAndRestoresOriginal_Enable_PutsThemBack()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        _fx.GameFile("BepInEx/plugins/Base.dll", "original");
        await _pipeline.InstallAsync(_fx.Instance(), Package(_fx.Package("m",
            ("BepInEx/plugins/Base.dll", "patched"), ("BepInEx/plugins/Extra.dll", "extra"))));

        // Act
        var disabled = await _pipeline.SetEnabledAsync(_fx.Instance(), "thunderstore:cool", false);

        // Assert
        disabled.Should().BeTrue();
        _fx.ReadGame("BepInEx/plugins/Base.dll").Should().Be("original");
        _fx.GameHas("BepInEx/plugins/Extra.dll").Should().BeFalse();

        // Act
        var enabled = await _pipeline.SetEnabledAsync(_fx.Instance(), "thunderstore:cool", true);

        // Assert
        enabled.Should().BeTrue();
        _fx.ReadGame("BepInEx/plugins/Base.dll").Should().Be("patched");
        _fx.ReadGame("BepInEx/plugins/Extra.dll").Should().Be("extra");

        // Uninstall after a disable/enable round trip still restores the original
        await _pipeline.UninstallAsync(_fx.Instance(), "thunderstore:cool");
        _fx.ReadGame("BepInEx/plugins/Base.dll").Should().Be("original");

        // ...and leaves no scratch, backup or parking folders behind, only the ledger
        var dataDir = _store.GetInstanceDataDirectory(_fx.Instance().Id);
        Directory.EnumerateFileSystemEntries(dataDir).Select(Path.GetFileName).Should().Equal("mods.json");
    }

    [Fact]
    public async Task LedgerStore_DoesNotPersistComputedProperties()
    {
        // Arrange
        _fx.GameFolder("BepInEx/plugins");
        await _pipeline.InstallAsync(_fx.Instance(), Package(_fx.Package("m", ("BepInEx/plugins/Cool.dll", "x"))));

        // Act
        var json = await File.ReadAllTextAsync(Path.Combine(_store.GetInstanceDataDirectory(_fx.Instance().Id), "mods.json"));

        // Assert
        json.Should().NotContain("ledgerId").And.NotContain("totalSizeBytes");
        (await _store.LoadAsync(_fx.Instance().Id)).Find("thunderstore:cool")!.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Install_FailureMidway_RollsBackEveryChange()
    {
        // Arrange: the second file is locked, so copying it fails after the first one was written
        _fx.GameFolder("BepInEx/plugins");
        _fx.GameFile("BepInEx/plugins/B.dll", "original-b");
        var pkg = _fx.Package("locked", ("BepInEx/plugins/A.dll", "a"), ("BepInEx/plugins/B.dll", "b"));

        Task<ModInstallResult> install;
        using (File.Open(Path.Combine(_fx.GameDir, "BepInEx", "plugins", "B.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Act
            install = _pipeline.InstallAsync(_fx.Instance(), Package(pkg));
            await install;
        }

        // Assert
        install.Result.Status.Should().Be(ModInstallStatus.Failed);
        _fx.GameHas("BepInEx/plugins/A.dll").Should().BeFalse();
        _fx.ReadGame("BepInEx/plugins/B.dll").Should().Be("original-b");
        (await _store.LoadAsync(_fx.Instance().Id)).Mods.Should().BeEmpty();
    }

    [Fact]
    public async Task LedgerStore_CorruptFile_IsSetAsideAndTreatedAsEmpty()
    {
        // Arrange
        var dir = _store.GetInstanceDataDirectory(_fx.Instance().Id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mods.json"), "{ not json");

        // Act
        var ledger = await _store.LoadAsync(_fx.Instance().Id);

        // Assert
        ledger.Mods.Should().BeEmpty();
        Directory.EnumerateFiles(dir, "mods.json.corrupt-*").Should().ContainSingle();
    }
}
