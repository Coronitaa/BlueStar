using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DepotCleanupTests
{
    [Fact]
    public async Task AppSettings_DeleteDepotsAfterInstall_DefaultsToTrue()
    {
        // Arrange
        var tempSettingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json");
        try
        {
            var logger = NullLogger<AppSettingsService>.Instance;
            var settingsService = new AppSettingsService(logger, tempSettingsPath);

            // Act & Assert
            settingsService.DeleteDepotsAfterInstall.Should().BeTrue();

            await settingsService.SetDeleteDepotsAfterInstallAsync(false);
            settingsService.DeleteDepotsAfterInstall.Should().BeFalse();
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempSettingsPath);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void CleanUpDownloadedDepots_DeletesWorkingDirStagingDirAndSourceArchive()
    {
        // Arrange
        var testId = Guid.NewGuid();
        var tempFolder = Path.Combine(Path.GetTempPath(), "BlueStarTests_" + testId);
        var installPath = Path.Combine(tempFolder, "GameInstall");
        var stagingPath = Path.Combine(installPath, ".DepotDownloader");
        var workingDir = Path.Combine(tempFolder, "DepotWork", testId.ToString());
        var sourceArchiveZip = Path.Combine(tempFolder, "game_source.zip");

        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(stagingPath);
        Directory.CreateDirectory(workingDir);

        File.WriteAllText(Path.Combine(stagingPath, "chunk.stg"), "dummy chunk data");
        File.WriteAllText(Path.Combine(workingDir, "manifest.manifest"), "dummy manifest data");
        File.WriteAllText(sourceArchiveZip, "dummy zip data");

        var instance = new GameInstance
        {
            Id = testId,
            Name = "Test Game",
            AppId = 12345,
            InstallPath = installPath,
            SourceArchivePath = sourceArchiveZip
        };

        var provider = new DepotDownloaderProvider(
            "dummy_path.exe",
            NullLogger<DepotDownloaderProvider>.Instance);

        // Act - Call reflection or invoke download cleanup
        var cleanupMethod = typeof(DepotDownloaderProvider)
            .GetMethod("CleanUpDownloadedDepots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        cleanupMethod.Should().NotBeNull();
        cleanupMethod!.Invoke(provider, new object[] { instance, workingDir });

        // Assert
        Directory.Exists(workingDir).Should().BeFalse("DepotWork directory should be deleted after installation");
        Directory.Exists(stagingPath).Should().BeFalse(".DepotDownloader staging directory should be deleted after installation");
        File.Exists(sourceArchiveZip).Should().BeFalse("Source archive zip should be deleted after installation");

        // Clean up test temp folder
        if (Directory.Exists(tempFolder))
        {
            Directory.Delete(tempFolder, recursive: true);
        }
    }
}
