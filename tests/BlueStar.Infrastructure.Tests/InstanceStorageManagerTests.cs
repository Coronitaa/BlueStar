using System;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Core.Storage;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class InstanceStorageManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly Win32Linker _linker;
    private readonly InstanceStorageManager _storageManager;

    public InstanceStorageManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_StorageMgrTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _linker = new Win32Linker(NullLogger<Win32Linker>.Instance);
        _storageManager = new InstanceStorageManager(_linker, NullLogger<InstanceStorageManager>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task CreateInstanceAsync_DeploysZeroCopyHardlinks_AndIsolatesMutableFolders()
    {
        var depotPath = Path.Combine(_testDir, "depot_730_base");
        var instancePath = Path.Combine(_testDir, "instance_player1");

        // Populate base depot with simulated game files
        Directory.CreateDirectory(Path.Combine(depotPath, "bin"));
        Directory.CreateDirectory(Path.Combine(depotPath, "Content", "Paks"));
        Directory.CreateDirectory(Path.Combine(depotPath, "steam_settings"));
        Directory.CreateDirectory(Path.Combine(depotPath, "saves"));

        var exePath = Path.Combine(depotPath, "game.exe");
        var dllPath = Path.Combine(depotPath, "bin", "engine.dll");
        var pakPath = Path.Combine(depotPath, "Content", "Paks", "pakchunk0.pak");
        var settingsPath = Path.Combine(depotPath, "steam_settings", "template.ini");
        var savePath = Path.Combine(depotPath, "saves", "default_save.dat");

        File.WriteAllText(exePath, "MZ Game Executable Binary Bytes");
        File.WriteAllText(dllPath, "MZ Dynamic Library Binary Bytes");
        File.WriteAllText(pakPath, "PAK Data Blob Binary Assets 1234567890");
        File.WriteAllText(settingsPath, "[Settings]\nMode=Online");
        File.WriteAllText(savePath, "Default Save Data");

        var result = await _storageManager.CreateInstanceAsync(depotPath, instancePath);

        result.Success.Should().BeTrue();
        if (_linker.SupportsHardLinks(instancePath))
        {
            result.IsZeroCopy.Should().BeTrue();
            result.TotalFilesLinked.Should().Be(3); // game.exe, engine.dll, pakchunk0.pak
            result.TotalFilesCopied.Should().Be(2); // template.ini, default_save.dat in isolated folders

            // Verify hardlink link counts
            var instExe = Path.Combine(instancePath, "game.exe");
            var instDll = Path.Combine(instancePath, "bin", "engine.dll");
            var instPak = Path.Combine(instancePath, "Content", "Paks", "pakchunk0.pak");
            var instSettings = Path.Combine(instancePath, "steam_settings", "template.ini");
            var instSave = Path.Combine(instancePath, "saves", "default_save.dat");

            _linker.GetFileLinkCount(instExe).Should().Be(2);
            _linker.GetFileLinkCount(instDll).Should().Be(2);
            _linker.GetFileLinkCount(instPak).Should().Be(2);

            // Mutable isolated files MUST have link count 1 (independent physical copy)
            _linker.GetFileLinkCount(instSettings).Should().Be(1);
            _linker.GetFileLinkCount(instSave).Should().Be(1);
        }
    }

    [Fact]
    public async Task BreakLinkAndCopyAsync_ImplementsCopyOnWrite_LeavingBaseDepotUntouched()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var depotPath = Path.Combine(_testDir, "base_depot");
        var instancePath = Path.Combine(_testDir, "instance_cow");

        Directory.CreateDirectory(depotPath);
        var originalFile = Path.Combine(depotPath, "config.json");
        File.WriteAllText(originalFile, "{\"setting\": \"original_depot_value\"}");

        await _storageManager.CreateInstanceAsync(depotPath, instancePath);

        var instanceFile = Path.Combine(instancePath, "config.json");
        _linker.GetFileLinkCount(instanceFile).Should().Be(2);

        // Break link (CoW) before mutating
        bool cowSuccess = await _storageManager.BreakLinkAndCopyAsync(instanceFile);
        cowSuccess.Should().BeTrue();

        _linker.GetFileLinkCount(instanceFile).Should().Be(1);
        _linker.GetFileLinkCount(originalFile).Should().Be(1);

        // Now modify the instance file
        await File.WriteAllTextAsync(instanceFile, "{\"setting\": \"modified_instance_value\"}");

        // Instance has modified content
        File.ReadAllText(instanceFile).Should().Be("{\"setting\": \"modified_instance_value\"}");

        // Base depot MUST remain completely untouched!
        File.ReadAllText(originalFile).Should().Be("{\"setting\": \"original_depot_value\"}");
    }

    [Fact]
    public async Task DeleteInstanceAsync_RemovesInstance_WithoutCorruptingDepot()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var depotPath = Path.Combine(_testDir, "safe_depot");
        var instancePath = Path.Combine(_testDir, "instance_to_delete");

        Directory.CreateDirectory(depotPath);
        File.WriteAllText(Path.Combine(depotPath, "core.dll"), "important core binary");

        await _storageManager.CreateInstanceAsync(depotPath, instancePath);

        _linker.GetFileLinkCount(Path.Combine(depotPath, "core.dll")).Should().Be(2);

        bool deleted = await _storageManager.DeleteInstanceAsync(instancePath);
        deleted.Should().BeTrue();

        Directory.Exists(instancePath).Should().BeFalse();

        // Base depot file MUST still exist with link count = 1
        File.Exists(Path.Combine(depotPath, "core.dll")).Should().BeTrue();
        _linker.GetFileLinkCount(Path.Combine(depotPath, "core.dll")).Should().Be(1);
        File.ReadAllText(Path.Combine(depotPath, "core.dll")).Should().Be("important core binary");
    }

    [Fact]
    public async Task GetStorageStatsAsync_CalculatesSharedVsUniqueDiskUsage()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var depotPath = Path.Combine(_testDir, "stats_depot");
        var instancePath = Path.Combine(_testDir, "stats_instance");

        Directory.CreateDirectory(depotPath);
        File.WriteAllText(Path.Combine(depotPath, "large_asset.pak"), new string('A', 5000));

        await _storageManager.CreateInstanceAsync(depotPath, instancePath);

        // Add a local unique file to the instance
        var settingsDir = Path.Combine(instancePath, "steam_settings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "user.txt"), new string('B', 100));

        var stats = await _storageManager.GetStorageStatsAsync(instancePath);

        stats.HardlinkedFileCount.Should().Be(1);
        stats.SharedHardlinkedBytes.Should().Be(5000);
        stats.UniqueFileCount.Should().Be(1);
        stats.UniqueAllocatedBytes.Should().Be(100);
        stats.TotalApparentBytes.Should().Be(5100);
    }
}
