using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Core.Storage;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Engine;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Mods;
using BlueStar.Infrastructure.Storage;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class PerformanceAndEdgeCaseTests : IDisposable
{
    private readonly string _testDir;
    private readonly Win32Linker _linker;
    private readonly InstanceStorageManager _storageManager;
    private readonly InstanceManager _instanceManager;
    private readonly ReFixManager _refixManager;
    private readonly UgcBridge _ugcBridge;

    public PerformanceAndEdgeCaseTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_PerfEdgeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _linker = new Win32Linker(NullLogger<Win32Linker>.Instance);
        _storageManager = new InstanceStorageManager(_linker, NullLogger<InstanceStorageManager>.Instance);
        _refixManager = new ReFixManager(NullLogger<ReFixManager>.Instance);
        _ugcBridge = new UgcBridge(_linker, NullLogger<UgcBridge>.Instance);

        var engineDetector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        _instanceManager = new InstanceManager(
            NullLogger<InstanceManager>.Instance,
            statsService: null,
            storageManager: _storageManager,
            engineDetector: engineDetector,
            refixManager: _refixManager,
            rootPath: Path.Combine(_testDir, "instances"),
            depotsRootPath: Path.Combine(_testDir, "depots"));
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
    public async Task ZeroCopyInstanceCreation_TakesUnder3Seconds_ForLargeGameDepot()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var depotPath = _instanceManager.GetBaseDepotPath(123456);
        Directory.CreateDirectory(Path.Combine(depotPath, "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(depotPath, "Engine", "Content"));
        Directory.CreateDirectory(Path.Combine(depotPath, "Game", "Content", "Paks"));

        // Create 200 simulated game files across subdirectories
        for (int i = 0; i < 200; i++)
        {
            var folder = (i % 3) switch
            {
                0 => Path.Combine(depotPath, "Binaries", "Win64"),
                1 => Path.Combine(depotPath, "Engine", "Content"),
                _ => Path.Combine(depotPath, "Game", "Content", "Paks")
            };
            File.WriteAllText(Path.Combine(folder, $"asset_{i}.pak"), $"Fake Asset Content {i}");
        }

        File.WriteAllText(Path.Combine(depotPath, "Binaries", "Win64", "Game-Win64-Shipping.exe"), "executable binary");

        var sw = Stopwatch.StartNew();
        var instance = await _instanceManager.CreateInstanceFromDepotAsync(123456, "LargeGameInstance", depotPath);
        sw.Stop();

        // Acceptance Criteria: deploy must be very fast (< 3 seconds)
        sw.Elapsed.TotalSeconds.Should().BeLessThan(3.0);

        instance.Should().NotBeNull();
        instance.Status.Should().Be(InstanceStatus.Ready);

        var stats = await _storageManager.GetStorageStatsAsync(instance.InstallPath);
        stats.HardlinkedFileCount.Should().BeGreaterOrEqualTo(201);
        stats.UniqueAllocatedBytes.Should().BeLessThan(50 * 1024 * 1024); // Less than 50MB
    }

    [Fact]
    public async Task ConcurrentInstances_CanReadHardlinksSimultaneously_WithoutFileLocks()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var depotPath = _instanceManager.GetBaseDepotPath(550);
        Directory.CreateDirectory(depotPath);
        var sharedAsset = Path.Combine(depotPath, "pak01_dir.vpk");
        File.WriteAllText(sharedAsset, "Shared Read-Only Game Data Across Instances");

        var inst1 = await _instanceManager.CreateInstanceFromDepotAsync(550, "Instance_A", depotPath);
        var inst2 = await _instanceManager.CreateInstanceFromDepotAsync(550, "Instance_B", depotPath);

        var file1 = Path.Combine(inst1.InstallPath, "pak01_dir.vpk");
        var file2 = Path.Combine(inst2.InstallPath, "pak01_dir.vpk");

        // Open both files concurrently for read
        using var fs1 = new FileStream(file1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fs2 = new FileStream(file2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        using var sr1 = new StreamReader(fs1);
        using var sr2 = new StreamReader(fs2);

        var content1 = await sr1.ReadToEndAsync();
        var content2 = await sr2.ReadToEndAsync();

        content1.Should().Be("Shared Read-Only Game Data Across Instances");
        content2.Should().Be("Shared Read-Only Game Data Across Instances");
    }

    [Fact]
    public async Task NonNtfsFallback_GracefullyCopies_WhenHardlinksDisabled()
    {
        var depotPath = Path.Combine(_testDir, "depot_fallback");
        var instancePath = Path.Combine(_testDir, "instance_fallback");

        Directory.CreateDirectory(depotPath);
        File.WriteAllText(Path.Combine(depotPath, "game.exe"), "exe content");
        File.WriteAllText(Path.Combine(depotPath, "data.bin"), "bin content");

        // Force fallback by setting allow fallback
        var options = new InstanceDeployOptions
        {
            AllowNonNtfsFallback = true
        };

        var result = await _storageManager.CreateInstanceAsync(depotPath, instancePath, options);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(instancePath, "game.exe")).Should().BeTrue();
        File.Exists(Path.Combine(instancePath, "data.bin")).Should().BeTrue();
    }
}
