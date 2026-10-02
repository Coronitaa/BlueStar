using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class SidebarAndNavigationInteractionTests
{
    private static void EnsureApplicationResources()
    {
        if (Application.Current == null)
        {
            new Application();
        }

        if (Application.Current != null && Application.Current.Resources.MergedDictionaries.Count == 0)
        {
            try
            {
                var dicts = new[]
                {
                    "pack://application:,,,/BlueStar;component/Themes/Colors.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Icons.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Typography.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Motion.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Controls.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/DarkTheme.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Strings.en.xaml"
                };

                foreach (var uri in dicts)
                {
                    Application.Current.Resources.MergedDictionaries.Add(
                        new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
                }
            }
            catch
            {
                // Fallback in case pack URI assembly loader is not registered in isolated test harness
                Application.Current.Resources["BaseBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(10, 10, 14));
                Application.Current.Resources["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Application.Current.Resources["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(160, 160, 170));
                Application.Current.Resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(100, 100, 110));
                Application.Current.Resources["SubtleBorderBrush"] = new SolidColorBrush(Color.FromRgb(30, 30, 40));
                Application.Current.Resources["SurfaceCardBrush"] = new SolidColorBrush(Color.FromRgb(20, 20, 28));
                Application.Current.Resources["SurfaceCardHoverBrush"] = new SolidColorBrush(Color.FromRgb(30, 30, 42));
                Application.Current.Resources["SurfaceCardElevatedBrush"] = new SolidColorBrush(Color.FromRgb(26, 26, 36));
                Application.Current.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                Application.Current.Resources["MainFontFamily"] = new FontFamily("Segoe UI");
            }
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
        {
            throw new AggregateException(ex);
        }
    }

    private static (MainViewModel MainVm, HomeViewModel HomeVm) CreateTestViewModels()
    {
        var mockDownloadProvider = new Mock<IDownloadProvider>();
        var mockInstanceManager = new Mock<IInstanceManager>();
        mockInstanceManager.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance>());

        var mockNotifications = new Mock<INotificationService>();
        var downloadQueueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstanceManager.Object,
            mockNotifications.Object);

        var mockSteamStatus = new Mock<ISteamStatusService>();
        mockSteamStatus.SetupGet(s => s.CurrentStatus).Returns(new SteamStatus(false, null, null, null));

        var mockUpdateService = new Mock<IUpdateService>();

        var taskCollection = new ObservableCollection<BackgroundTaskItem>();
        var mockBackgroundTaskService = new Mock<IBackgroundTaskService>();
        mockBackgroundTaskService.SetupGet(b => b.Tasks).Returns(new ReadOnlyObservableCollection<BackgroundTaskItem>(taskCollection));

        var mockApiClient = new Mock<IDepotBoxApiClient>();
        var mockArchiveParser = new Mock<IDepotBoxArchiveParser>();
        var mockEngineDetector = new Mock<IEngineDetector>();
        var mockGameLauncher = new Mock<IGameLauncher>();

        // Build DI container for App.Services
        var services = new ServiceCollection();

        var homeVm = new HomeViewModel(
            mockInstanceManager.Object,
            downloadQueueManager,
            mockGameLauncher.Object,
            mockApiClient.Object,
            mockArchiveParser.Object,
            mockEngineDetector.Object,
            NullLogger<HomeViewModel>.Instance,
            notificationService: mockNotifications.Object);

        services.AddSingleton(homeVm);
        services.AddTransient<LibraryViewModel>(_ => new LibraryViewModel(
            mockInstanceManager.Object,
            mockArchiveParser.Object,
            mockEngineDetector.Object,
            mockGameLauncher.Object,
            NullLogger<LibraryViewModel>.Instance));
        services.AddTransient<InstanceDetailViewModel>(_ => new InstanceDetailViewModel(
            mockInstanceManager.Object,
            new Mock<IDlcInstaller>().Object,
            downloadQueueManager,
            new BlueStar.Infrastructure.Storage.AppSettingsService(NullLogger<BlueStar.Infrastructure.Storage.AppSettingsService>.Instance),
            mockEngineDetector.Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            mockGameLauncher.Object,
            NullLogger<InstanceDetailViewModel>.Instance));

        var sp = services.BuildServiceProvider();
        App.SetTestServices(sp);

        var mainVm = new MainViewModel(
            downloadQueueManager,
            mockSteamStatus.Object,
            mockNotifications.Object,
            mockUpdateService.Object,
            mockInstanceManager.Object,
            mockBackgroundTaskService.Object,
            gameLauncher: mockGameLauncher.Object,
            apiClient: mockApiClient.Object);

        return (mainVm, homeVm);
    }

    [Fact]
    public void SidebarImportToggle_TogglesIsSidebarImportMenuOpen()
    {
        RunOnStaThread(() =>
        {
            var (mainVm, _) = CreateTestViewModels();
            Assert.False(mainVm.IsSidebarImportMenuOpen);

            mainVm.ToggleSidebarImportMenuCommand.Execute(null);
            Assert.True(mainVm.IsSidebarImportMenuOpen);

            mainVm.ToggleSidebarImportMenuCommand.Execute(null);
            Assert.False(mainVm.IsSidebarImportMenuOpen);

            mainVm.IsSidebarImportMenuOpen = true;
            mainVm.CloseSidebarImportMenuCommand.Execute(null);
            Assert.False(mainVm.IsSidebarImportMenuOpen);
        });
    }

    [Fact]
    public void HomeImportToggle_TogglesIsImportMenuOpen()
    {
        RunOnStaThread(() =>
        {
            var (_, homeVm) = CreateTestViewModels();
            Assert.False(homeVm.IsImportMenuOpen);

            homeVm.ToggleImportMenuCommand.Execute(null);
            Assert.True(homeVm.IsImportMenuOpen);

            homeVm.ToggleImportMenuCommand.Execute(null);
            Assert.False(homeVm.IsImportMenuOpen);
        });
    }

    [Fact]
    public void SidebarImportZip_OpensModalOnHomeViewModel_WithoutNavigatingAwayFromActiveTab()
    {
        RunOnStaThread(() =>
        {
            var (mainVm, homeVm) = CreateTestViewModels();

            // Simulate user being on Library tab
            mainVm.Navigate("Library");
            Assert.Equal("Library", mainVm.SelectedNavigation);

            mainVm.IsSidebarImportMenuOpen = true;

            // Trigger ZIP import from Sidebar "+"
            mainVm.SidebarImportZipCommand.Execute(null);

            // Active tab and navigation must remain Library!
            Assert.Equal("Library", mainVm.SelectedNavigation);
            Assert.False(mainVm.IsSidebarImportMenuOpen);

            // Modal must be open on HomeViewModel
            Assert.True(homeVm.IsZipImportModalOpen);
        });
    }

    [Fact]
    public void SidebarImportFolder_OpensModalOnHomeViewModel_WithoutNavigatingAwayFromActiveTab()
    {
        RunOnStaThread(() =>
        {
            var (mainVm, homeVm) = CreateTestViewModels();

            // Simulate user being on Library tab
            mainVm.Navigate("Library");
            Assert.Equal("Library", mainVm.SelectedNavigation);

            mainVm.IsSidebarImportMenuOpen = true;

            // Trigger Folder import from Sidebar "+"
            mainVm.SidebarImportFolderCommand.Execute(null);

            // Active tab and navigation must remain Library!
            Assert.Equal("Library", mainVm.SelectedNavigation);
            Assert.False(mainVm.IsSidebarImportMenuOpen);

            // Modal must be open on HomeViewModel
            Assert.True(homeVm.IsFolderImportModalOpen);
        });
    }

    [Fact]
    public void SelectedRecentInstanceId_TracksActiveInstance_AndSynchronizesWithNavigation()
    {
        RunOnStaThread(() =>
        {
            var (mainVm, _) = CreateTestViewModels();
            var instanceId = Guid.NewGuid();
            var testInstance = new GameInstance
            {
                Id = instanceId,
                Name = "Test Game",
                AppId = 12345,
                InstallPath = @"C:\Games\Test"
            };

            // Initially on Home, no instance selected
            Assert.Null(mainVm.SelectedRecentInstanceId);

            // Open instance detail
            mainVm.OpenInstanceDetail(testInstance);
            Assert.Equal(instanceId, mainVm.SelectedRecentInstanceId);
            Assert.Equal("Test Game", mainVm.CurrentInstanceTitle);

            // Navigate to Library
            mainVm.Navigate("Library");
            Assert.Null(mainVm.SelectedRecentInstanceId);
            Assert.Null(mainVm.CurrentInstanceTitle);

            // Go back in history -> should restore InstanceDetail selection!
            Assert.True(mainVm.CanGoBack);
            mainVm.GoBackCommand.Execute(null);
            Assert.Equal(instanceId, mainVm.SelectedRecentInstanceId);
            Assert.Equal("Test Game", mainVm.CurrentInstanceTitle);

            // Go forward in history -> should clear selection again!
            Assert.True(mainVm.CanGoForward);
            mainVm.GoForwardCommand.Execute(null);
            Assert.Null(mainVm.SelectedRecentInstanceId);
        });
    }
}
