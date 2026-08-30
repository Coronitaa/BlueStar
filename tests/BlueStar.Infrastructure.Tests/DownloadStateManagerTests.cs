using System;
using System.IO;
using System.Linq;
using BlueStar.Infrastructure.Downloader;
using DepotDownloader;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DownloadStateManagerTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly DownloadStateManager _stateManager;

    public DownloadStateManagerTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "BlueStarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testBaseDir);
        _stateManager = new DownloadStateManager(NullLogger<DownloadStateManager>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBaseDir))
                Directory.Delete(_testBaseDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void DownloadStateSnapshot_SaveAndLoad_RoundTripsCorrectly()
    {
        var instanceId = Guid.NewGuid();
        var filePath = Path.Combine(_testBaseDir, "download_state.json");

        var snapshot = new DownloadStateSnapshot
        {
            InstanceId = instanceId,
            AppId = 730,
            InstallPath = @"C:\Games\CSGO",
            TotalBytes = 1024 * 1024 * 500,
            DownloadedBytes = 1024 * 1024 * 250,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Depots =
            [
                new DepotDownloadState
                {
                    DepotId = 731,
                    ManifestId = 123456789UL,
                    TotalBytes = 1024 * 1024 * 500,
                    DownloadedBytes = 1024 * 1024 * 250,
                    TotalChunks = 100,
                    CompletedChunks = 50,
                    CompletedChunkIds = ["chunk1", "chunk2"],
                    IsComplete = false
                }
            ]
        };

        // Save
        snapshot.SaveToFile(filePath);
        File.Exists(filePath).Should().BeTrue();

        // Load
        var loaded = DownloadStateSnapshot.LoadFromFile(filePath);
        loaded.Should().NotBeNull();
        loaded!.InstanceId.Should().Be(instanceId);
        loaded.AppId.Should().Be(730);
        loaded.InstallPath.Should().Be(@"C:\Games\CSGO");
        loaded.TotalBytes.Should().Be(1024 * 1024 * 500);
        loaded.Depots.Should().HaveCount(1);
        loaded.Depots[0].DepotId.Should().Be(731);
        loaded.Depots[0].CompletedChunkIds.Should().Contain("chunk1").And.Contain("chunk2");

        // Delete
        DownloadStateSnapshot.DeleteFile(filePath);
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public void DownloadStateSnapshot_LoadFromNonExistent_ReturnsNull()
    {
        var nonExistentPath = Path.Combine(_testBaseDir, "non_existent.json");
        var result = DownloadStateSnapshot.LoadFromFile(nonExistentPath);
        result.Should().BeNull();
    }
}
