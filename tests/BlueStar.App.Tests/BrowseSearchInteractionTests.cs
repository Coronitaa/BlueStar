using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using BlueStar.App;
using BlueStar.App.ViewModels;
using BlueStar.App.Views;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class BrowseSearchInteractionTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly AppSettingsService _appSettings;
    private readonly Mock<IDepotBoxApiClient> _mockApiClient = new();
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();
    private readonly Mock<IDepotBoxArchiveParser> _mockArchiveParser = new();
    private readonly Mock<IEngineDetector> _mockEngineDetector = new();
    private readonly Mock<IGameLauncher> _mockGameLauncher = new();
    private readonly Mock<IDownloadProvider> _mockDownloadProvider = new();
    private readonly Mock<ISteamStatusService> _mockSteamStatus = new();
    private readonly Mock<INotificationService> _mockNotification = new();
    private readonly Mock<IUpdateService> _mockUpdate = new();
    private readonly Mock<IBackgroundTaskService> _mockBackgroundTask = new();
    private readonly Mock<IDepotBoxAuthService> _mockAuth = new();
    private readonly Mock<ILicenseService> _mockLicense = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();

    public BrowseSearchInteractionTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"browse_test_{Guid.NewGuid():N}.json");
        _appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempSettingsPath);
        _mockLocalization.SetupGet(l => l.CurrentLanguage).Returns("en");
        _mockSteamStatus.SetupGet(s => s.CurrentStatus).Returns(new SteamStatus(false, null, null, null));
        var taskCollection = new ObservableCollection<BackgroundTaskItem>();
        _mockBackgroundTask.SetupGet(b => b.Tasks).Returns(new ReadOnlyObservableCollection<BackgroundTaskItem>(taskCollection));
        _mockInstanceManager.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GameInstance>());
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempSettingsPath))
                File.Delete(_tempSettingsPath);
        }
        catch { }
    }

    private void ConfigureServices(ServiceCollection services, DownloadQueueManager downloadQueueManager)
    {
        var homeVm = new HomeViewModel(
            _mockInstanceManager.Object,
            downloadQueueManager,
            _mockGameLauncher.Object,
            _mockApiClient.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<HomeViewModel>.Instance,
            notificationService: _mockNotification.Object);

        services.AddSingleton(homeVm);
    }

    [Fact]
    public void BrowseView_CanBeConstructedWithoutXamlParseException()
    {
        StaTestHelper.Run(() =>
        {
            var view = new BrowseView();
            view.Should().NotBeNull();
        });
    }

    [Fact]
    public void BrowseViewModel_NavigateToSettingsPreferencesCommand_InvokesCallback()
    {
        // Arrange
        var vm = new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            settingsService: _appSettings,
            autoSearch: false);

        bool callbackInvoked = false;
        vm.OnNavigateToSettingsPreferencesRequested = () => callbackInvoked = true;

        // Act
        vm.NavigateToSettingsPreferencesCommand.Execute(null);

        // Assert
        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public void MainViewModel_NavigateToSettingsContentFilters_NavigatesToSettingsAndExpandsContentFilters()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var settingsVm = new SettingsViewModel(
                _mockAuth.Object,
                _mockApiClient.Object,
                _mockLicense.Object,
                _mockNotification.Object,
                _appSettings,
                _mockLocalization.Object,
                NullLogger<SettingsViewModel>.Instance);

            settingsVm.IsContentFiltersExpanded = false;

            var downloadQueueManager = new DownloadQueueManager(_mockDownloadProvider.Object, NullLogger<DownloadQueueManager>.Instance, _mockInstanceManager.Object, _mockNotification.Object);

            var services = new ServiceCollection();
            services.AddSingleton(settingsVm);
            ConfigureServices(services, downloadQueueManager);
            App.SetTestServices(services.BuildServiceProvider());

            using var mainVm = new MainViewModel(
                downloadQueueManager,
                _mockSteamStatus.Object,
                _mockNotification.Object,
                _mockUpdate.Object,
                _mockInstanceManager.Object,
                _mockBackgroundTask.Object,
                appSettings: _appSettings);

            // Act
            mainVm.NavigateToSettingsContentFilters();

            // Assert
            mainVm.SelectedNavigation.Should().Be("Settings");
            mainVm.CurrentView.Should().NotBeNull();
            mainVm.CurrentView.Should().BeOfType<SettingsView>();
            settingsVm.IsContentFiltersExpanded.Should().BeTrue();
        });
    }

    [Fact]
    public void BrowseViewModel_WhenWiredToMainViewModel_DirectlyTriggersNavigateToSettingsContentFilters()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var settingsVm = new SettingsViewModel(
                _mockAuth.Object,
                _mockApiClient.Object,
                _mockLicense.Object,
                _mockNotification.Object,
                _appSettings,
                _mockLocalization.Object,
                NullLogger<SettingsViewModel>.Instance);

            settingsVm.IsContentFiltersExpanded = false;

            var browseVm = new BrowseViewModel(
                _mockApiClient.Object,
                _mockInstanceManager.Object,
                _mockArchiveParser.Object,
                _mockEngineDetector.Object,
                NullLogger<BrowseViewModel>.Instance,
                settingsService: _appSettings,
                autoSearch: false);

            var downloadQueueManager = new DownloadQueueManager(_mockDownloadProvider.Object, NullLogger<DownloadQueueManager>.Instance, _mockInstanceManager.Object, _mockNotification.Object);

            var services = new ServiceCollection();
            services.AddSingleton(settingsVm);
            services.AddSingleton(browseVm);
            ConfigureServices(services, downloadQueueManager);
            App.SetTestServices(services.BuildServiceProvider());

            using var mainVm = new MainViewModel(
                downloadQueueManager,
                _mockSteamStatus.Object,
                _mockNotification.Object,
                _mockUpdate.Object,
                _mockInstanceManager.Object,
                _mockBackgroundTask.Object,
                appSettings: _appSettings);

            // Wire browseVm to mainVm as done in GetExploreView()
            browseVm.OnNavigateToSettingsPreferencesRequested = mainVm.NavigateToSettingsContentFilters;

            // Act: Click "Preferences" button in Explore view banner
            browseVm.NavigateToSettingsPreferencesCommand.Execute(null);

            // Assert: MainViewModel navigated to Settings and opened Content Filters expander
            mainVm.SelectedNavigation.Should().Be("Settings");
            mainVm.CurrentView.Should().BeOfType<SettingsView>();
            settingsVm.IsContentFiltersExpanded.Should().BeTrue();
        });
    }
}
