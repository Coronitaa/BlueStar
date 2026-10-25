using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public class InstanceDetailCustomizationTests
{
    private static InstanceDetailViewModel CreateViewModel(GameInstance instance, Mock<IDlcInstaller>? mockDlcInstaller = null)
    {
        var mockInstanceManager = new Mock<IInstanceManager>();
        mockInstanceManager.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance gi, CancellationToken _) => gi);

        var mockDownloadProvider = new Mock<IDownloadProvider>();
        var mockNotifications = new Mock<INotificationService>();
        var mockGameLauncher = new Mock<IGameLauncher>();
        var mockEngineDetector = new Mock<IEngineDetector>();

        var downloadQueueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstanceManager.Object,
            mockNotifications.Object);

        var tempSettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bluestar_test_settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);
        var dlcInstaller = mockDlcInstaller?.Object ?? new Mock<IDlcInstaller>().Object;

        var vm = new InstanceDetailViewModel(
            mockInstanceManager.Object,
            dlcInstaller,
            downloadQueueManager,
            appSettings,
            mockEngineDetector.Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            mockGameLauncher.Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            notificationService: mockNotifications.Object);

        vm.Instance = instance;
        return vm;
    }

    [Fact]
    public void ImportedFolderWithoutSteamAssociation_HidesVersionsAndPrerequisitesTabs()
    {
        var unassociatedInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Standalone Folder Game",
            AppId = 0,
            InstallPath = @"C:\Games\StandaloneGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(unassociatedInstance);

        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsVersionsTabVisible);
        Assert.False(vm.IsDepotBoxTabsVisible);
        Assert.False(vm.IsPrerequisitesTabVisible);

        // Cannot switch to hidden tabs
        vm.SwitchTab("Files");
        Assert.NotEqual("Files", vm.SelectedTab);

        vm.SwitchTab("Prerequisites");
        Assert.NotEqual("Prerequisites", vm.SelectedTab);
    }

    [Fact]
    public void ImportedFolderWithSpacewarAppId_HidesVersionsAndPrerequisitesTabs()
    {
        var spacewarInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Folder Game With Spacewar",
            AppId = 480,
            InstallPath = @"C:\Games\SpacewarGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(spacewarInstance);

        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsVersionsTabVisible);
        Assert.False(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public void ImportedFolderWithSteamAssociation_ShowsVersionsAndPrerequisitesTabs()
    {
        var steamAssociatedFolderInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Cyberpunk 2077",
            AppId = 1091500,
            InstallPath = @"C:\Games\Cyberpunk2077",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(steamAssociatedFolderInstance);

        Assert.False(vm.IsFolderWithoutSteamAssociation);
        Assert.True(vm.IsVersionsTabVisible);
        Assert.True(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public void DepotBoxInstance_ShowsVersionsAndPrerequisitesTabs()
    {
        var depotInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Hollow Knight",
            AppId = 367520,
            InstallPath = @"C:\Games\HollowKnight",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(depotInstance);

        Assert.False(vm.IsFolderWithoutSteamAssociation);
        Assert.True(vm.IsVersionsTabVisible);
        Assert.True(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public async Task ForceInstallAllDlcs_UnlocksInstallDlcsButton_AndForcesInstallationToAllDlcs()
    {
        var dlc1 = new DlcInfo { AppId = 1001, Name = "DLC 1", Depots = [], IsInstalled = false };
        var dlc2 = new DlcInfo { AppId = 1002, Name = "DLC 2", Depots = [], IsInstalled = false };

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\TestGame",
            Origin = InstanceOrigin.DepotBox,
            Dlcs = [dlc1, dlc2],
            Status = InstanceStatus.Ready,
            ForceInstallAllDlcs = false
        };

        var mockDlcInstaller = new Mock<IDlcInstaller>();
        mockDlcInstaller.Setup(d => d.InstallDlcAsync(
                It.IsAny<GameInstance>(),
                It.IsAny<DlcInfo>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>()))
            .ReturnsAsync(true);

        var vm = CreateViewModel(instance, mockDlcInstaller);
        vm.Dlcs = new ObservableCollection<SelectableDlcItem>
        {
            new SelectableDlcItem { Dlc = dlc1, IsSelected = false },
            new SelectableDlcItem { Dlc = dlc2, IsSelected = false }
        };

        // Initially no DLC selected and ForceInstallAllDlcs is false: button is locked (disabled)
        Assert.False(vm.CanDownloadAndInstallDlcs);
        Assert.False(vm.DownloadAndInstallDlcsCommand.CanExecute(null));

        // When user confirms ForceInstallAllDlcs (or ticks it):
        vm.DoNotShowForceAllDlcsWarning = true;
        await vm.ConfirmForceAllDlcsWarningCommand.ExecuteAsync(null);

        // Tick is active and button is unlocked!
        Assert.True(vm.ForceInstallAllDlcs);
        Assert.True(vm.CanDownloadAndInstallDlcs);
        Assert.True(vm.DownloadAndInstallDlcsCommand.CanExecute(null));

        // When command is executed, it forces installation for all DLCs
        await vm.DownloadAndInstallDlcsCommand.ExecuteAsync(null);

        // Both DLCs in the collection are now marked as unlocked and selected
        Assert.All(vm.Dlcs, item => Assert.True(item.IsSelected));
        Assert.All(vm.Dlcs, item => Assert.True(item.IsUnlocked));
    }

    [Fact]
    public void ImportedFolderWithoutSteamAssociation_HidesDetectedVersion()
    {
        var folderUnassociated = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Folder Game",
            AppId = 0,
            InstallPath = @"C:\Games\FolderGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(folderUnassociated);
        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsDetectedVersionVisible);

        var spacewarInstance = folderUnassociated with { AppId = 480 };
        var vmSpacewar = CreateViewModel(spacewarInstance);
        Assert.True(vmSpacewar.IsFolderWithoutSteamAssociation);
        Assert.False(vmSpacewar.IsDetectedVersionVisible);
    }

    [Fact]
    public void SteamOrAssociatedInstances_ShowDetectedVersion()
    {
        var steamInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Steam Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vmSteam = CreateViewModel(steamInstance);
        Assert.False(vmSteam.IsFolderWithoutSteamAssociation);
        Assert.True(vmSteam.IsDetectedVersionVisible);

        var associatedFolderInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Associated Folder Game",
            AppId = 1245620,
            InstallPath = @"C:\Games\EldenRing",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vmAssociated = CreateViewModel(associatedFolderInstance);
        Assert.False(vmAssociated.IsFolderWithoutSteamAssociation);
        Assert.True(vmAssociated.IsDetectedVersionVisible);
    }

    [Fact]
    public async Task ForceInstallAllDlcs_OpensWarningModal_AndOnlyAppliesWhenConfirmed()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\TestGame",
            Origin = InstanceOrigin.DepotBox,
            Dlcs = [],
            Status = InstanceStatus.Ready,
            ForceInstallAllDlcs = false
        };

        var vm = CreateViewModel(instance);

        Assert.False(vm.ForceInstallAllDlcs);
        Assert.False(vm.IsForceAllDlcsWarningOpen);

        // When user toggles ForceInstallAllDlcs to true:
        vm.ForceInstallAllDlcs = true;

        // Modal opens and ForceInstallAllDlcs is NOT yet applied
        Assert.True(vm.IsForceAllDlcsWarningOpen);
        Assert.False(vm.ForceInstallAllDlcs);

        // If user cancels:
        vm.CancelForceAllDlcsWarningCommand.Execute(null);
        Assert.False(vm.IsForceAllDlcsWarningOpen);
        Assert.False(vm.ForceInstallAllDlcs);

        // When user toggles again and confirms:
        vm.ForceInstallAllDlcs = true;
        Assert.True(vm.IsForceAllDlcsWarningOpen);

        await vm.ConfirmForceAllDlcsWarningCommand.ExecuteAsync(null);
        Assert.False(vm.IsForceAllDlcsWarningOpen);
        Assert.True(vm.ForceInstallAllDlcs);
    }


    [Fact]
    public void ArtworkSlots_AreInitializedWithCorrectTypesAndTitles()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Artwork Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        Assert.NotNull(vm.CapsuleArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.Capsule, vm.CapsuleArtwork.SlotType);

        Assert.NotNull(vm.HeroArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.Hero, vm.HeroArtwork.SlotType);

        Assert.NotNull(vm.LogoArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.Logo, vm.LogoArtwork.SlotType);

        Assert.NotNull(vm.HeaderArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.Header, vm.HeaderArtwork.SlotType);

        Assert.NotNull(vm.IconArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.Icon, vm.IconArtwork.SlotType);

        Assert.NotNull(vm.InstanceBannerArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.InstanceBanner, vm.InstanceBannerArtwork.SlotType);

        Assert.NotNull(vm.InstanceLogoArtwork);
        Assert.Equal(BlueStar.Core.Helpers.SteamArtworkType.InstanceLogo, vm.InstanceLogoArtwork.SlotType);
    }

    [Fact]
    public async Task ProcessNewArtworkFile_InvalidDimensions_TriggersWarningModal_AndCanBeCancelled()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Artwork Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artwork_test_{Guid.NewGuid():N}.png");
        try
        {
            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x0D });
                bw.Write(System.Text.Encoding.ASCII.GetBytes("IHDR"));
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x64 }); // 100
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x64 }); // 100
                bw.Write(new byte[] { 0x08, 0x06, 0x00, 0x00, 0x00 });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
                bw.Write(System.Text.Encoding.ASCII.GetBytes("IEND"));
                bw.Write(new byte[] { 0xAE, 0x42, 0x60, 0x82 });
                System.IO.File.WriteAllBytes(tempFile, ms.ToArray());
            }

            Assert.False(vm.IsArtworkDimensionModalOpen);

            await vm.ProcessNewArtworkFileAsync(BlueStar.Core.Helpers.SteamArtworkType.Capsule, tempFile);

            Assert.True(vm.IsArtworkDimensionModalOpen);
            Assert.False(string.IsNullOrWhiteSpace(vm.PendingArtworkWarningMessage));

            vm.CancelPendingArtworkCommand.Execute(null);
            Assert.False(vm.IsArtworkDimensionModalOpen);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ProcessNewArtworkFile_InvalidDimensions_Confirmed_AppliesArtworkAndClosesModal()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Artwork Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artwork_test_confirm_{Guid.NewGuid():N}.png");
        try
        {
            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x0D });
                bw.Write(System.Text.Encoding.ASCII.GetBytes("IHDR"));
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x64 }); // 100
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x64 }); // 100
                bw.Write(new byte[] { 0x08, 0x06, 0x00, 0x00, 0x00 });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
                bw.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
                bw.Write(System.Text.Encoding.ASCII.GetBytes("IEND"));
                bw.Write(new byte[] { 0xAE, 0x42, 0x60, 0x82 });
                System.IO.File.WriteAllBytes(tempFile, ms.ToArray());
            }

            await vm.ProcessNewArtworkFileAsync(BlueStar.Core.Helpers.SteamArtworkType.Capsule, tempFile);
            Assert.True(vm.IsArtworkDimensionModalOpen);

            await vm.ConfirmPendingArtworkCommand.ExecuteAsync(null);

            Assert.False(vm.IsArtworkDimensionModalOpen);
            Assert.Null(vm.PendingArtworkBytes);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ProcessNewArtworkFile_InstanceLogo_UpdatesInstanceCustomIconPathAndSlot()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Logo Test Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artwork_logo_{Guid.NewGuid():N}.png");
        try
        {
            // Valid 1x1 PNG bytes (square 1:1 aspect ratio)
            byte[] validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            System.IO.File.WriteAllBytes(tempFile, validPng);

            await vm.ProcessNewArtworkFileAsync(BlueStar.Core.Helpers.SteamArtworkType.InstanceLogo, tempFile);

            Assert.NotNull(vm.Instance.CustomIconPath);
            Assert.True(System.IO.File.Exists(vm.Instance.CustomIconPath));
            Assert.NotNull(vm.InstanceLogoArtwork);
            Assert.True(vm.InstanceLogoArtwork.HasImage);
            Assert.NotNull(vm.InstanceLogoArtwork.ImageSource);
            Assert.Equal(vm.Instance.CustomIconPath, vm.Instance.NavLogoUrl);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }


    [Fact]
    public void InstallModal_AdvancedOptions_ToggleAndGenerateSteamId_WorkCorrectly()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Adv Options Game",
            AppId = 480,
            InstallPath = @"C:\Games\AdvGame",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.NotInstalled
        };

        var vm = CreateViewModel(instance);

        Assert.False(vm.IsInstallModalAdvancedOptionsExpanded);
        Assert.Equal("▶", vm.AdvancedOptionsChevron);

        vm.ToggleInstallModalAdvancedOptionsCommand.Execute(null);
        Assert.True(vm.IsInstallModalAdvancedOptionsExpanded);
        Assert.Equal("▼", vm.AdvancedOptionsChevron);

        vm.GenerateInstallModalRandomSteamIdCommand.Execute(null);
        Assert.NotNull(vm.InstallModalCustomSteamId);
        Assert.StartsWith("76561198", vm.InstallModalCustomSteamId);
        Assert.Equal(17, vm.InstallModalCustomSteamId.Length);

        vm.ResetInstallModalEmulatorIdentityCommand.Execute(null);
        Assert.Equal(Environment.UserName, vm.InstallModalCustomUsername);
        Assert.Null(vm.InstallModalCustomSteamId);
    }

    [Fact]
    public async Task ArtworkSlots_InstanceBannerAndHeader_RemainIndependent()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Banner Separation Game",
            AppId = 480,
            InstallPath = @"C:\Games\BannerSeparationGame",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        var tempHeaderFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artwork_hdr_{Guid.NewGuid():N}.png");
        var tempBannerFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artwork_bnr_{Guid.NewGuid():N}.png");
        try
        {
            byte[] validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            System.IO.File.WriteAllBytes(tempHeaderFile, validPng);
            System.IO.File.WriteAllBytes(tempBannerFile, validPng);

            // 1. Applying to Header must NOT modify Instance.CustomBannerPath or InstanceBannerArtwork
            await vm.ProcessNewArtworkFileAsync(BlueStar.Core.Helpers.SteamArtworkType.Header, tempHeaderFile);
            if (vm.IsArtworkDimensionModalOpen)
            {
                await vm.ConfirmPendingArtworkAsync();
            }

            Assert.True(vm.HeaderArtwork?.HasImage);
            Assert.Null(vm.Instance.CustomBannerPath);
            Assert.False(vm.InstanceBannerArtwork?.HasImage ?? false);

            // 2. Applying to InstanceBanner updates CustomBannerPath and InstanceBannerArtwork without overwriting HeaderArtwork
            await vm.ProcessNewArtworkFileAsync(BlueStar.Core.Helpers.SteamArtworkType.InstanceBanner, tempBannerFile);
            if (vm.IsArtworkDimensionModalOpen)
            {
                await vm.ConfirmPendingArtworkAsync();
            }

            Assert.NotNull(vm.Instance.CustomBannerPath);
            Assert.True(System.IO.File.Exists(vm.Instance.CustomBannerPath));
            Assert.True(vm.InstanceBannerArtwork?.HasImage);
            Assert.True(vm.HeaderArtwork?.HasImage);
        }
        finally
        {
            if (System.IO.File.Exists(tempHeaderFile)) System.IO.File.Delete(tempHeaderFile);
            if (System.IO.File.Exists(tempBannerFile)) System.IO.File.Delete(tempBannerFile);
        }
    }

    [Fact]
    public void ImageCropHelper_CropsImageToRequestedAspectRatio()
    {
        // Create a 200x100 (2:1) test PNG
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        byte[] originalBytes = ms.ToArray();

        // Target ratio for Capsule is 600 / 900 = 0.6667 (vertical)
        double targetCapsule = 600.0 / 900.0;
        var (croppedBytes, newW, newH, ext) = BlueStar.App.Helpers.ImageCropHelper.CropToAspectRatio(originalBytes, targetCapsule, ".png");

        Assert.NotEmpty(croppedBytes);
        Assert.True(newW > 0);
        Assert.True(newH > 0);
        double achievedRatio = (double)newW / newH;
        Assert.True(Math.Abs(achievedRatio - targetCapsule) / targetCapsule < 0.02);
    }

    [Fact]
    public void ImageCropHelper_TestJpegCrop()
    {
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        byte[] pngBytes = ms.ToArray();

        double target = 460.0 / 215.0;
        var (cropped, w, h, ext) = BlueStar.App.Helpers.ImageCropHelper.CropToAspectRatio(pngBytes, target, ".jpg");
        Assert.Equal(".jpg", ext);
        Assert.NotEmpty(cropped);
        Assert.True(w > 0);
        Assert.True(h > 0);
    }

    [Fact]
    public void CoverBannerImage_UpdatesWhenInstanceBannerSlotChanges()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Banner Refresh Test Game",
            AppId = 0,
            InstallPath = @"C:\Games\Test",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(instance);

        // Initially null cover banner image since AppId is 0 and no custom banner
        Assert.Null(vm.CoverBannerImage);

        // Create a 200x100 test image bytes
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        byte[] imgBytes = ms.ToArray();

        // Load into InstanceBannerArtwork
        vm.InstanceBannerArtwork?.LoadFromBytes(imgBytes, @"C:\dummy\instance_banner.png");

        // CoverBannerImage immediately reflects it
        Assert.NotNull(vm.CoverBannerImage);
        Assert.True(vm.InstanceBannerArtwork?.HasImage);

        // Clear slot
        vm.InstanceBannerArtwork?.LoadFromFile(null);
        Assert.False(vm.InstanceBannerArtwork?.HasImage);
    }

    [Fact]
    public void ImageCacheService_Invalidate_PurgesCache()
    {
        var cache = BlueStar.App.Services.ImageCacheService.Instance;
        var dummyUrl = "https://cdn.example.com/test_image_purge.jpg";

        // Calling Invalidate should execute safely and remove keys without throwing
        cache.Invalidate(dummyUrl, 999999);
        var result = cache.GetFromMemoryOrDisk(dummyUrl, 999999);
        Assert.Null(result);
    }

    [Fact]
    public void GridBanner_IsIndependentFromInstanceBanner()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Grid Independence Game",
            AppId = 440,
            InstallPath = @"C:\Games\TF2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready,
            CustomBannerPath = @"C:\dummy\instance_banner.png"
        };

        // Grid banner (HeaderImageUrl) MUST NOT depend on instance_banner.png
        Assert.NotEqual(@"C:\dummy\instance_banner.png", instance.HeaderImageUrl);
        Assert.Contains("440", instance.HeaderImageUrl);

        // When CustomGridBannerPath is set, HeaderImageUrl reflects it
        var withGridBanner = instance with { CustomGridBannerPath = @"C:\dummy\grid_header.jpg" };
        Assert.Equal(@"C:\dummy\grid_header.jpg", withGridBanner.HeaderImageUrl);
    }

    [Fact]
    public void InstanceStatusChange_NotifiesIsInstalledAndButtons()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Status Notification Game",
            AppId = 440,
            InstallPath = @"C:\Games\TF2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.NotInstalled
        };

        var vm = CreateViewModel(instance);
        Assert.False(vm.IsInstalled);

        var changedProperties = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProperties.Add(e.PropertyName);
        };

        vm.Instance = instance with { Status = InstanceStatus.Ready };

        Assert.True(vm.IsInstalled);
        Assert.Contains(nameof(vm.IsInstalled), changedProperties);
        Assert.Contains(nameof(vm.CanDeployEmulator), changedProperties);
        Assert.Contains(nameof(vm.ShowDownloadButton), changedProperties);
        Assert.Contains(nameof(vm.DownloadButtonText), changedProperties);
    }

    [Fact]
    public async Task LoadArtworksAsync_InitializesInstanceBannerFromHeaderIfMissing()
    {
        var instanceId = Guid.NewGuid();
        var artworkDir = BlueStar.Core.Helpers.InstanceArtworkService.GetArtworkDirectory(instanceId);
        System.IO.Directory.CreateDirectory(artworkDir);

        var headerPath = System.IO.Path.Combine(artworkDir, "header.jpg");
        var bannerPath = System.IO.Path.Combine(artworkDir, "instance_banner.jpg");

        try
        {
            // Create a valid 200x100 dummy JPEG for header.jpg
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(headerPath))
            {
                encoder.Save(fs);
            }

            var instance = new GameInstance
            {
                Id = instanceId,
                Name = "Artwork Test Game",
                AppId = 440,
                InstallPath = @"C:\Games\TF2",
                Origin = InstanceOrigin.Steam,
                Status = InstanceStatus.Ready
            };

            var vm = CreateViewModel(instance);

            // Prior to loading, InstanceBannerArtwork has no image
            Assert.False(vm.InstanceBannerArtwork?.HasImage);

            await vm.LoadArtworksAsync();

            // InstanceBannerArtwork is initialized from header and has image loaded
            Assert.True(System.IO.File.Exists(bannerPath));
            Assert.True(vm.InstanceBannerArtwork?.HasImage);
            Assert.NotNull(vm.CoverBannerImage);
        }
        finally
        {
            if (System.IO.Directory.Exists(artworkDir))
            {
                try { System.IO.Directory.Delete(artworkDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task LoadArtworksAsync_InitializesHeaderFromInstanceBannerIfMissing()
    {
        var instanceId = Guid.NewGuid();
        var artworkDir = BlueStar.Core.Helpers.InstanceArtworkService.GetArtworkDirectory(instanceId);
        System.IO.Directory.CreateDirectory(artworkDir);

        var headerPath = System.IO.Path.Combine(artworkDir, "header.jpg");
        var bannerPath = System.IO.Path.Combine(artworkDir, "instance_banner.jpg");

        try
        {
            // Create a valid dummy JPEG for instance_banner.jpg (616x353 main capsule)
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(616, 353, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(bannerPath))
            {
                encoder.Save(fs);
            }

            var instance = new GameInstance
            {
                Id = instanceId,
                Name = "Artwork Test Game",
                AppId = 440,
                InstallPath = @"C:\Games\TF2",
                Origin = InstanceOrigin.Steam,
                Status = InstanceStatus.Ready
            };

            var vm = CreateViewModel(instance);

            // Prior to loading, HeaderArtwork has no image
            Assert.False(vm.HeaderArtwork?.HasImage);

            await vm.LoadArtworksAsync();

            // HeaderArtwork is initialized from instance_banner and has image loaded
            Assert.True(System.IO.File.Exists(headerPath));
            Assert.True(vm.HeaderArtwork?.HasImage);
            Assert.True(vm.InstanceBannerArtwork?.HasImage);
            Assert.NotNull(vm.CoverBannerImage);
        }
        finally
        {
            if (System.IO.Directory.Exists(artworkDir))
            {
                try { System.IO.Directory.Delete(artworkDir, recursive: true); } catch { }
            }
        }
    }
}
