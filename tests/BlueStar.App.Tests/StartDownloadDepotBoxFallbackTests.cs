using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

/// <summary>
/// Regression: an instance that already held some manifest (e.g. the shared redistributable 228989) skipped the
/// DepotBox archive fetch, so depots only DepotBox has (no manifest on ManifestHub, archive only on DepotBox) failed with
/// "Cannot Download ... None of the configured providers has data" even though the bundled key works.
/// </summary>
public sealed class StartDownloadDepotBoxFallbackTests : IDisposable
{
    private const uint AppId = 900100;
    private const uint MainDepot = 900101;
    private const ulong MainManifest = 1234567890123456789;
    private const string DepotKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private readonly GameInstance _instance;
    private readonly string _instanceDir;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bluestar_fallback_{Guid.NewGuid():N}");

    public StartDownloadDepotBoxFallbackTests()
    {
        _instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = AppId,
            InstallPath = Path.Combine(_tempDir, "install"),
            Depots = new[]
            {
                new DepotInfo { DepotId = MainDepot, ManifestId = MainManifest, Name = "Base Game Content", SizeBytes = 1024 }
            }
        };
        _instanceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", _instance.Id.ToString());

        // A valid manifest for an unrelated depot makes the up-front archive fetch skip itself.
        var manifestDir = Path.Combine(_instanceDir, "manifests");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllBytes(Path.Combine(manifestDir, "228989_5753583882400741046.manifest"), new byte[256]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_instanceDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task StartDownload_FetchesDepotBoxArchive_WhenDepotHasNoManifestOrKeyButInstanceHasOtherManifests()
    {
        // Arrange
        var archivePath = Path.Combine(_tempDir, $"{AppId}.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create)) { }

        var apiClient = new Mock<IDepotBoxApiClient>();
        apiClient.Setup(c => c.DownloadArchiveAsync(AppId, It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivePath);

        var parser = new Mock<IDepotBoxArchiveParser>();
        parser.Setup(p => p.ParseAsync(archivePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DepotBoxArchive
            {
                AppId = AppId,
                Games = new[]
                {
                    new DepotBoxGame
                    {
                        AppId = AppId,
                        DepotKey = DepotKey,
                        Depots = new[] { new DepotBoxDepot { DepotId = MainDepot, ManifestId = MainManifest } }
                    }
                }
            });

        var keyRepo = new Mock<IDepotKeyRepository>();
        var notifications = new Mock<INotificationService>();
        var vm = await CreateViewModelAsync(apiClient.Object, parser.Object, keyRepo.Object, notifications);
        foreach (var depot in vm.Depots) depot.IsSelected = true;

        // Act
        try
        {
            await vm.StartDownloadCommand.ExecuteAsync(null);
        }
        catch (Exception)
        {
            // Queueing past the depot check needs services this test does not wire; only the check matters here.
        }

        // Assert
        apiClient.Verify(c => c.DownloadArchiveAsync(AppId, It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        keyRepo.Verify(r => r.RegisterKeyAsync(MainDepot, DepotKey, "DepotBox", It.IsAny<CancellationToken>()), Times.Once);
        notifications.Verify(n => n.ShowError("Cannot Download", It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<string?>(), It.IsAny<Action?>()), Times.Never);
    }

    [Fact]
    public async Task StartDownload_ReportsDepotBoxFailure_InsteadOfClaimingNoProviderHasData()
    {
        // Arrange
        var apiClient = new Mock<IDepotBoxApiClient>();
        apiClient.Setup(c => c.DownloadArchiveAsync(AppId, It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("DepotBox API: Invalid or missing API key."));

        var notifications = new Mock<INotificationService>();
        var vm = await CreateViewModelAsync(apiClient.Object, new Mock<IDepotBoxArchiveParser>().Object, new Mock<IDepotKeyRepository>().Object, notifications);
        foreach (var depot in vm.Depots) depot.IsSelected = true;

        // Act
        await vm.StartDownloadCommand.ExecuteAsync(null);

        // Assert
        notifications.Verify(n => n.ShowError("Cannot Download",
            It.Is<string>(s => s.Contains("DepotBox could not be reached") && s.Contains("Invalid or missing API key")), It.IsAny<TimeSpan?>(), It.IsAny<string?>(), It.IsAny<Action?>()), Times.Once);
    }

    private async Task<InstanceDetailViewModel> CreateViewModelAsync(
        IDepotBoxApiClient apiClient,
        IDepotBoxArchiveParser parser,
        IDepotKeyRepository keyRepo,
        Mock<INotificationService> notifications)
    {
        var instanceManager = new Mock<IInstanceManager>();
        instanceManager.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance gi, CancellationToken _) => gi);

        var queue = new DownloadQueueManager(
            new Mock<IDownloadProvider>().Object,
            NullLogger<DownloadQueueManager>.Instance,
            instanceManager.Object,
            notifications.Object);

        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var vm = new InstanceDetailViewModel(
            instanceManager.Object,
            new Mock<IDlcInstaller>().Object,
            queue,
            new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath),
            new Mock<IEngineDetector>().Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            new Mock<IGameLauncher>().Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            notificationService: notifications.Object,
            apiClient: apiClient,
            archiveParser: parser,
            manifestRegistry: new Mock<IManifestRegistry>().Object,
            depotKeyRepository: keyRepo);

        await vm.LoadInstanceAsync(_instance);
        return vm;
    }
}
