using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.DepotBox;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure;

/// <summary>
/// Every non-UI service BlueStar needs, shared by the WPF head and any other head (Avalonia, headless --launch).
/// The caller still registers logging, localization and its own ViewModels.
/// </summary>
public static class BlueStarServiceCollectionExtensions
{
    public static IServiceCollection AddBlueStar(this IServiceCollection services)
    {
        // Infrastructure services
        services.AddSingleton<BlueStar.Infrastructure.Storage.AppSettingsService>();
        services.AddSingleton<INotificationService, BlueStar.Infrastructure.Services.NotificationService>();
        services.AddSingleton<IBackgroundTaskService, BlueStar.Infrastructure.Services.BackgroundTaskService>();
        services.AddSingleton<ITagsService, BlueStar.Infrastructure.Services.TagsService>();
        services.AddSingleton<IEmulatorLifecycleService, BlueStar.Infrastructure.Services.EmulatorLifecycleService>();
        services.AddSingleton<ILicenseService, BlueStar.Infrastructure.Services.LicenseService>();
        if (OperatingSystem.IsWindows())
            services.AddSingleton<ISecureStorage, SecureStorage>();
        else
            services.AddSingleton<ISecureStorage, LinuxSecureStorage>();
        services.AddSingleton<IDepotBoxAuthService, BlueStar.Infrastructure.Services.DepotBoxAuthService>();
        services.AddSingleton<ICacheService, FileCacheService>();
        services.AddSingleton<IInstanceManager, InstanceManager>();
        services.AddSingleton<DepotBoxLuaParser>();
        services.AddSingleton<BlueStar.Infrastructure.DepotBox.DepotBoxKeyImporter>();

        services.AddSingleton<IDepotBoxArchiveParser, DepotBoxArchiveParser>();
        services.AddSingleton<IDlcInstaller, BlueStar.Infrastructure.Dlc.CreamInstallerService>();
        services.AddSingleton<DownloadStateManager>();
        services.AddSingleton<IDownloadProvider, BlueStar.Infrastructure.Downloader.DepotDownloaderProvider>();
        services.AddSingleton<IUpdateService, BlueStar.Infrastructure.Update.GitHubUpdateService>();
        const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        services.AddHttpClient<BlueStar.Infrastructure.Services.CommunityStatsService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html, */*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,es;q=0.8");
        });
        services.AddSingleton<ICommunityStatsService>(sp => sp.GetRequiredService<BlueStar.Infrastructure.Services.CommunityStatsService>());

        // HTTP clients
        services.AddHttpClient<IDepotBoxApiClient, DepotBoxApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://depotbox.org");
            client.Timeout = TimeSpan.FromMinutes(15); // ZIPs are built on-the-fly
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/1.2.3");
        });

        services.AddHttpClient<SteamStoreApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://store.steampowered.com");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,es;q=0.8");
        });
        services.AddSingleton<IMetadataProvider>(sp => sp.GetRequiredService<SteamStoreApiClient>());

        // Explore: faceted Steam store search and the tag catalog behind the filter panel.
        services.AddHttpClient<ISteamCatalogSearchService, BlueStar.Infrastructure.Steam.SteamCatalogSearchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,es;q=0.8");
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        });

        services.AddHttpClient<ISteamTagCatalogService, BlueStar.Infrastructure.Steam.SteamTagCatalogService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*; q=0.01");
        });

        // Local SQLite Catalog & Search Pipeline
        var catalogDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar",
            "catalog.db");
        services.AddSingleton<ILocalCatalogRepository>(sp =>
            new LocalCatalogRepository(sp.GetRequiredService<ILogger<LocalCatalogRepository>>(), catalogDbPath));
        services.AddHttpClient<ICatalogSnapshotService, SteamCatalogSnapshotService>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromMinutes(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        });
        services.AddSingleton<SteamResponseValidator>();
        services.AddSingleton<ISearchPipeline, SearchPipeline>();

        // General HTTP Client
        services.AddHttpClient();

        // Storage, Linking, and Multi-Instance Services
        services.AddSingleton(sp => UnixLinker.ForCurrentOs(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<BlueStar.Core.Storage.IInstanceStorageManager, BlueStar.Infrastructure.Storage.InstanceStorageManager>();
        services.AddSingleton<IReFixManager, BlueStar.Infrastructure.Emulators.ReFixManager>();
        services.AddSingleton<IModLoaderProvisioner, BlueStar.Infrastructure.Mods.ModLoaderProvisioner>();
        services.AddSingleton<IUgcBridge, BlueStar.Infrastructure.Workshop.UgcBridge>();
        services.AddSingleton<IHeuristicModDispatcher, BlueStar.Infrastructure.Workshop.HeuristicModDispatcher>();
        services.AddSingleton<IWorkshopDownloader, BlueStar.Infrastructure.Workshop.WorkshopDownloader>();

        // Engine & Modding & Emulator services
        services.AddSingleton<IEngineDetector, BlueStar.Infrastructure.Engine.EngineDetector>();
        services.AddHttpClient<IBepInExService, BlueStar.Infrastructure.Mods.BepInExService>();
        services.AddHttpClient<BlueStar.Infrastructure.Workshop.SteamCmdWorkshopClient>();
        services.AddHttpClient<IWorkshopService, BlueStar.Infrastructure.Workshop.WorkshopService>();
        services.AddSingleton<IModLedgerStore, BlueStar.Infrastructure.Mods.Installation.JsonModLedgerStore>();
        services.AddSingleton<IModPlacementResolver, BlueStar.Infrastructure.Mods.Installation.ModPlacementResolver>();
        services.AddSingleton<IModInstallPipeline, BlueStar.Infrastructure.Mods.Installation.ModInstallPipeline>();
        services.AddSingleton<IWorkshopModFetcher, BlueStar.Infrastructure.Workshop.WorkshopModFetcher>();
        services.AddHttpClient<IThunderstoreService, BlueStar.Infrastructure.Mods.Thunderstore.ThunderstoreService>();
        services.AddHttpClient<ICurseForgeService, BlueStar.Infrastructure.Mods.CurseForge.CurseForgeService>();
        services.AddHttpClient<INexusModsService, BlueStar.Infrastructure.Mods.NexusMods.NexusModsService>();
        services.AddHttpClient<IModApiKeyService, BlueStar.Infrastructure.Mods.ModApiKeyService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<ISteamStatusService, BlueStar.Infrastructure.Steam.SteamStatusService>();
        services.AddSingleton<IModManager, BlueStar.Infrastructure.Mods.UnrealModManager>();
        services.AddSingleton<IModManager, BlueStar.Infrastructure.Mods.UnityModManager>();
        services.AddSingleton<IModManager, BlueStar.Infrastructure.Mods.GenericModManager>();
        services.AddSingleton<IModManagerRegistry, BlueStar.Infrastructure.Mods.ModManagerRegistry>();
        services.AddSingleton<IEmulator, BlueStar.Infrastructure.Emulators.ReFixEmulator>();
        services.AddSingleton<IEmulator, BlueStar.Infrastructure.Emulators.SmokeApiEmulator>();
        services.AddSingleton<IEmulatorRegistry, BlueStar.Infrastructure.Emulators.EmulatorRegistry>();
        services.AddHttpClient<IEmulatorRatingService, BlueStar.Infrastructure.Emulators.EmulatorRatingService>();
        services.AddHttpClient<IReFixUpdateService, BlueStar.Infrastructure.Emulators.ReFixUpdateService>();
        services.AddHttpClient<IPrerequisiteService, BlueStar.Infrastructure.Services.PrerequisiteService>();
        services.AddSingleton<IGameFixDeployService, BlueStar.Infrastructure.Services.GameFixDeployService>();
        if (OperatingSystem.IsLinux())
            services.AddSingleton<BlueStar.Infrastructure.Compat.CompatLauncher>();
        services.AddSingleton<IGameLauncher, BlueStar.Infrastructure.Launcher.GameLauncherService>();

        // Provider-Agnostic Multi-Source Architecture Services
        services.AddSingleton<IManifestCacheService, BlueStar.Infrastructure.Cache.ManifestCacheService>();
        services.AddSingleton<IDepotKeyRepository, BlueStar.Infrastructure.Storage.DepotKeyRepository>();

        // Network coordination & observability
        services.AddSingleton<IRequestCoordinator, BlueStar.Infrastructure.Services.RequestCoordinator>();
        services.AddSingleton<INetworkMetricsObserver, BlueStar.Infrastructure.Services.NetworkMetricsObserver>();

        // Manifest Providers
        services.AddSingleton<BlueStar.Infrastructure.Providers.Manifest.LocalCacheManifestProvider>();
        services.AddSingleton<BlueStar.Infrastructure.Providers.Manifest.DepotBoxManifestProvider>();
        services.AddSingleton<BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider>();

        // Multi-Provider Manifest Registry
        services.AddSingleton<IManifestRegistry>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<BlueStar.Infrastructure.Services.ManifestRegistry>>();
            var cache = sp.GetRequiredService<IManifestCacheService>();
            var coordinator = sp.GetRequiredService<IRequestCoordinator>();
            var metrics = sp.GetService<INetworkMetricsObserver>();
            var providers = new IManifestProvider[]
            {
                sp.GetRequiredService<BlueStar.Infrastructure.Providers.Manifest.LocalCacheManifestProvider>(),
                sp.GetRequiredService<BlueStar.Infrastructure.Providers.Manifest.DepotBoxManifestProvider>(),
                sp.GetRequiredService<BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider>()
            };
            return new BlueStar.Infrastructure.Services.ManifestRegistry(providers, cache, logger, coordinator, metrics);
        });


        // Provider-Agnostic Catalog, Curation, Builds & Planner
        services.AddSingleton<IGameCatalogProvider, BlueStar.Infrastructure.Providers.Catalog.SteamStoreCatalogProvider>();
        services.AddSingleton<IRecommendationProvider, BlueStar.Infrastructure.Providers.Curation.CommunityCurationProvider>();
        services.AddSingleton<IBuildResolver>(sp =>
        {
            var steamClient = sp.GetRequiredService<SteamStoreApiClient>();
            var registry = sp.GetRequiredService<IManifestRegistry>();
            var logger = sp.GetRequiredService<ILogger<BlueStar.Infrastructure.Services.BuildResolver>>();
            var curation = sp.GetService<IRecommendationProvider>();
            var keys = sp.GetService<IDepotKeyRepository>();
            var coordinator = sp.GetRequiredService<IRequestCoordinator>();
            var cache = sp.GetService<ICacheService>();
            return new BlueStar.Infrastructure.Services.BuildResolver(steamClient, registry, logger, curation, keys, coordinator, cache);
        });
        services.AddSingleton<IInstallationPlanner, BlueStar.Infrastructure.Services.InstallationPlanner>();
        services.AddHttpClient<BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider>();
        services.AddSingleton<IFixProvider>(sp => sp.GetRequiredService<BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider>());

        services.AddSingleton<DownloadQueueManager>();

        return services;
    }
}
