using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.DepotBox;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class GameFixDeployServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _gameDir;
    private readonly string _cacheDir;

    public GameFixDeployServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "BlueStar_FixTests", Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempRoot, "TestGame");
        _cacheDir = Path.Combine(_tempRoot, "Cache");
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ParseGameFixes_ArrayResponse_ParsesCorrectly()
    {
        var json = """
        [
            {
                "id": "007_First_Light_bypass",
                "name": "007 First Light Bypass",
                "downloadName": "007_First_Light_bypass.zip",
                "tags": ["bypass", "hypervisor"],
                "sizeBytes": 10485760,
                "description": "Bypass for 007 First Light"
            },
            {
                "id": "halo_onlinefix",
                "name": "Halo OnlineFix",
                "downloadName": "halo_onlinefix.zip",
                "tags": ["online"],
                "sizeBytes": 5242880
            }
        ]
        """;

        var fixes = DepotBoxApiClient.ParseGameFixes(json);

        Assert.Equal(2, fixes.Count);
        Assert.Equal("007_First_Light_bypass", fixes[0].Id);
        Assert.True(fixes[0].IsBypass);
        Assert.True(fixes[0].IsHypervisor);
        Assert.False(fixes[0].IsOnline);
        Assert.Equal("BYPASS + HYPERVISOR", fixes[0].TagsSummary);

        Assert.Equal("halo_onlinefix", fixes[1].Id);
        Assert.True(fixes[1].IsOnline);
        Assert.False(fixes[1].IsBypass);
    }

    [Fact]
    public void ParseGameFixes_WrappedInObject_ParsesCorrectly()
    {
        var json = """
        {
            "success": true,
            "fixes": [
                {
                    "fixId": "game_hypervisor",
                    "title": "Universal Hypervisor Emulation",
                    "filename": "universal_hypervisor.zip",
                    "tags": "hypervisor, bypass",
                    "size_bytes": 2048000
                }
            ]
        }
        """;

        var fixes = DepotBoxApiClient.ParseGameFixes(json);

        Assert.Single(fixes);
        Assert.Equal("game_hypervisor", fixes[0].Id);
        Assert.Equal("Universal Hypervisor Emulation", fixes[0].Name);
        Assert.True(fixes[0].IsHypervisor);
        Assert.True(fixes[0].IsBypass);
    }

    [Fact]
    public void ParseGameFixes_DepotBoxGamesResponse_ParsesCorrectly()
    {
        var json = """
        {
            "success": true,
            "tags": ["online", "bypass", "hypervisor"],
            "count": 1,
            "games": [
                {
                    "appid": "3357650",
                    "name": "PRAGMATA",
                    "headerImage": "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/3357650/header.jpg",
                    "capsuleImage": "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/3357650/capsule_231x87.jpg",
                    "fixes": [
                        {
                            "id": "12fbbb1be29121d2",
                            "downloadName": "PRAGMATA_bypass.zip",
                            "filename": "PRAGMATA_bypass.zip",
                            "size": "5.9 MB",
                            "badges": ["Bypass"],
                            "tags": ["bypass"]
                        }
                    ]
                }
            ]
        }
        """;

        var fixes = DepotBoxApiClient.ParseGameFixes(json);

        Assert.Single(fixes);
        Assert.Equal("12fbbb1be29121d2", fixes[0].Id);
        Assert.Equal("PRAGMATA", fixes[0].Name);
        Assert.Equal("PRAGMATA_bypass.zip", fixes[0].DownloadName);
        Assert.True(fixes[0].IsBypass);
        Assert.NotNull(fixes[0].SizeBytes);
        Assert.True(fixes[0].SizeBytes > 5_000_000);
    }

    [Fact]
    public async Task DeployFixAsync_And_UninstallFixLayer_WorksCleanly()
    {
        // 1. Setup mock game files
        var originalSteamApi = Path.Combine(_gameDir, "steam_api64.dll");
        await File.WriteAllTextAsync(originalSteamApi, "ORIGINAL_STEAM_API_V1");

        // 2. Create mock fix ZIP
        var fixZipPath = Path.Combine(_cacheDir, "test_bypass.zip");
        using (var zip = ZipFile.Open(fixZipPath, ZipArchiveMode.Create))
        {
            var entry1 = zip.CreateEntry("steam_api64.dll");
            using (var writer = new StreamWriter(entry1.Open()))
            {
                writer.Write("BYPASS_STEAM_API_V2");
            }

            var entry2 = zip.CreateEntry("bypass_hook.ini");
            using (var writer = new StreamWriter(entry2.Open()))
            {
                writer.Write("Enabled=1\nMode=Hypervisor");
            }
        }

        var fixInfo = new GameFixInfo
        {
            Id = "test_bypass",
            Name = "Test Bypass Fix",
            DownloadName = "test_bypass.zip",
            Tags = ["bypass", "hypervisor"]
        };

        var instance = new GameInstance
        {
            Name = "Test Game",
            InstallPath = _gameDir,
            ExecutablePath = Path.Combine(_gameDir, "game.exe")
        };

        var mockApiClient = new MockDepotBoxApiClient(fixZipPath);
        var mockInstanceManager = new MockInstanceManager(instance);
        var mockDlcInstaller = new MockDlcInstaller();

        var deployService = new GameFixDeployService(
            mockApiClient,
            mockInstanceManager,
            mockDlcInstaller,
            NullLogger<GameFixDeployService>.Instance);

        // 3. Deploy Fix
        var deployResult = await deployService.DeployFixAsync(instance, fixInfo, null, CancellationToken.None);
        Assert.True(deployResult);

        // Verify game files were replaced / created
        var currentSteamApi = await File.ReadAllTextAsync(originalSteamApi);
        Assert.Equal("BYPASS_STEAM_API_V2", currentSteamApi);

        var hookFile = Path.Combine(_gameDir, "bypass_hook.ini");
        Assert.True(File.Exists(hookFile));

        // Verify layer is recorded in instance
        var updatedInstance = mockInstanceManager.CurrentInstance;
        Assert.Single(updatedInstance.InstalledFixLayers);
        var layer = updatedInstance.InstalledFixLayers[0];
        Assert.Equal("test_bypass", layer.FixId);
        Assert.True(layer.IsBypass);
        Assert.True(layer.IsHypervisor);
        Assert.NotNull(layer.BackupDirectory);
        Assert.True(Directory.Exists(layer.BackupDirectory));

        // 4. Uninstall Fix Layer
        var uninstallResult = await deployService.UninstallFixLayerAsync(updatedInstance, layer, null, CancellationToken.None);
        Assert.True(uninstallResult);

        // Verify original steam_api64.dll restored and hook file removed
        var restoredSteamApi = await File.ReadAllTextAsync(originalSteamApi);
        Assert.Equal("ORIGINAL_STEAM_API_V1", restoredSteamApi);
        Assert.False(File.Exists(hookFile));

        var finalInstance = mockInstanceManager.CurrentInstance;
        Assert.Empty(finalInstance.InstalledFixLayers);
    }

    [Fact]
    public async Task MultiLayer_Stacking_Bypass_Plus_Hypervisor_Coexists()
    {
        // 1. Setup original game file
        var targetFile = Path.Combine(_gameDir, "game_module.dll");
        await File.WriteAllTextAsync(targetFile, "ORIGINAL_BASE");

        // 2. Create Fix 1 (Bypass)
        var fix1Zip = Path.Combine(_cacheDir, "fix1_bypass.zip");
        using (var zip = ZipFile.Open(fix1Zip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("game_module.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("FIX1_BYPASS");
        }

        // 3. Create Fix 2 (Hypervisor)
        var fix2Zip = Path.Combine(_cacheDir, "fix2_hypervisor.zip");
        using (var zip = ZipFile.Open(fix2Zip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("hypervisor_core.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("FIX2_HYPERVISOR");
        }

        var instance = new GameInstance
        {
            Name = "Stack Game",
            InstallPath = _gameDir
        };

        var mockApiClient = new MockDepotBoxApiClient(fix1Zip);
        var mockInstanceManager = new MockInstanceManager(instance);
        var mockDlcInstaller = new MockDlcInstaller();

        var deployService = new GameFixDeployService(
            mockApiClient,
            mockInstanceManager,
            mockDlcInstaller,
            NullLogger<GameFixDeployService>.Instance);

        // Deploy Fix 1 (Bypass)
        var fix1 = new GameFixInfo { Id = "fix1", Name = "Bypass Fix", DownloadName = "fix1_bypass.zip", Tags = ["bypass"] };
        await deployService.DeployFixAsync(instance, fix1, null, CancellationToken.None);

        // Update mock client to serve Fix 2
        mockApiClient.SetZipPath(fix2Zip);

        // Deploy Fix 2 (Hypervisor) on top
        var fix2 = new GameFixInfo { Id = "fix2", Name = "Hypervisor Fix", DownloadName = "fix2_hypervisor.zip", Tags = ["hypervisor"] };
        await deployService.DeployFixAsync(mockInstanceManager.CurrentInstance, fix2, null, CancellationToken.None);

        var stackedInstance = mockInstanceManager.CurrentInstance;
        Assert.Equal(2, stackedInstance.InstalledFixLayers.Count);
        Assert.Contains(stackedInstance.InstalledFixLayers, l => l.IsBypass);
        Assert.Contains(stackedInstance.InstalledFixLayers, l => l.IsHypervisor);

        // Verify both files exist
        Assert.Equal("FIX1_BYPASS", await File.ReadAllTextAsync(targetFile));
        Assert.True(File.Exists(Path.Combine(_gameDir, "hypervisor_core.dll")));

        // Uninstall all layers
        await deployService.UninstallAllFixLayersAsync(stackedInstance, null, CancellationToken.None);

        // Verify original restored and all fix files removed
        Assert.Equal("ORIGINAL_BASE", await File.ReadAllTextAsync(targetFile));
        Assert.False(File.Exists(Path.Combine(_gameDir, "hypervisor_core.dll")));
        Assert.Empty(mockInstanceManager.CurrentInstance.InstalledFixLayers);
    }

    private class MockDepotBoxApiClient : IDepotBoxApiClient
    {
        private string _zipPath;

        public MockDepotBoxApiClient(string zipPath) => _zipPath = zipPath;

        public void SetZipPath(string zipPath) => _zipPath = zipPath;

        public Task<string> DownloadGameFixAsync(string fixIdOrFilename, string targetPath, IProgress<DownloadProgress>? progress = null, string? downloadName = null, CancellationToken ct = default)
        {
            var dest = Path.Combine(targetPath, Path.GetFileName(_zipPath));
            File.Copy(_zipPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<IReadOnlyList<GameFixInfo>> GetGameFixesAsync(string? query = null, string? tags = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GameFixInfo>>([]);

        public Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct) => throw new NotImplementedException();
        public Task<GameMetadata?> GetGameAsync(uint appId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> CheckAvailabilityAsync(uint appId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IDictionary<uint, bool>> BatchCheckAvailabilityAsync(IEnumerable<uint> appIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> DownloadArchiveAsync(uint appId, string targetPath, IProgress<DownloadProgress>? progress, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> StartAsyncDownloadAsync(uint appId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DownloadProgress> CheckDownloadStatusAsync(string downloadToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> DownloadCompletedArchiveAsync(string downloadToken, string targetPath, CancellationToken ct) => throw new NotImplementedException();
    }

    private class MockInstanceManager : IInstanceManager
    {
        public GameInstance CurrentInstance { get; private set; }

        public MockInstanceManager(GameInstance instance) => CurrentInstance = instance;

        public Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct = default)
        {
            CurrentInstance = instance;
            return Task.FromResult(instance);
        }

        public Task<GameInstance?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<GameInstance?>(CurrentInstance);
        public Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameInstance>>([CurrentInstance]);
        public Task<GameInstance> CreateAsync(GameInstance instance, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GameInstance> CreateInstanceFromDepotAsync(uint appId, string instanceName, string depotPath, string? customInstancePath = null, BlueStar.Core.Storage.InstanceDeployOptions? deployOptions = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GameInstance> CloneInstanceAsync(Guid sourceInstanceId, string newInstanceName, CancellationToken ct = default) => throw new NotImplementedException();
        public string GetBaseDepotPath(uint appId) => string.Empty;
        public event EventHandler? InstancesChanged { add { } remove { } }
    }

    private class MockDlcInstaller : IDlcInstaller
    {
        public Task<bool> InstallDlcAsync(GameInstance instance, DlcInfo dlc, CancellationToken ct = default, IProgress<string>? progress = null) => Task.FromResult(true);
        public Task<bool> UninstallDlcAsync(GameInstance instance, DlcInfo dlc, CancellationToken ct = default, IProgress<string>? progress = null) => Task.FromResult(true);
        public Task<bool> IsDlcInstalledAsync(GameInstance instance, DlcInfo dlc, CancellationToken ct = default) => Task.FromResult(false);
    }
}
