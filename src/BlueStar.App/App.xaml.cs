using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.DepotBox;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BlueStar.App;

/// <summary>
/// Application entry point with DI configuration and single-instance mutex enforcement.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private const string MutexId = "Global\\BlueStar_SingleInstance_Mutex_9F4F";

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Gets the application service provider.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexId, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            var broughtForward = false;

            try
            {
                IntPtr hWnd = FindWindow(null, "BlueStar");
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindowAsync(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                    broughtForward = true;
                }
            }
            catch
            {
                // Ignore Win32 errors
            }

            // Another instance holds the mutex but has no window to raise: it is stuck, or it is
            // still shutting down. Exiting mutely here is what made that state impossible to
            // diagnose — no window, no error, and nothing in the log either, because the logger
            // is only configured further down. Say so instead.
            if (!broughtForward)
            {
                MessageBox.Show(
                    "BlueStar is already running, but it has no window to bring forward.\n\n" +
                    "It is either still closing or it has got stuck. End the BlueStar process in " +
                    "Task Manager and start it again.",
                    "BlueStar is already running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Environment.Exit(0);
            return;
        }


        // Initialize Debug & Diagnostics Service
        var debugLogService = new BlueStar.Infrastructure.Services.DebugLogService();

        // Configure Serilog with noise suppression for high-frequency framework polling
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "logs", "bluestar-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .WriteTo.Sink(new BlueStar.App.Services.DebugLogSink(debugLogService))
            .CreateLogger();

        this.DispatcherUnhandledException += (s, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled Dispatcher Exception: {Message}", args.Exception.Message);
            Console.WriteLine($"[FATAL DISPATCHER ERROR] {args.Exception}");
        };

        AppDomain.CurrentDomain.FirstChanceException += (s, args) =>
        {
            if (args.Exception is InvalidOperationException ex && ex.Message.Contains("Foreground"))
            {
                Log.Fatal(ex, "[FIRST CHANCE FOREGROUND ERROR] {Message}\nStackTrace:\n{StackTrace}", ex.Message, ex.StackTrace);
                Console.WriteLine($"[FIRST CHANCE FOREGROUND ERROR] {ex.Message}\n{ex.StackTrace}");
            }
        };

        // Build DI container
        var services = new ServiceCollection();

        // Diagnostics & Debugging
        services.AddSingleton<IDebugLogService>(debugLogService);

        // Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // Infrastructure services
        services.AddSingleton<BlueStar.Infrastructure.Storage.AppSettingsService>();
        services.AddSingleton<ILocalizationService, BlueStar.App.Services.LocalizationService>();
        services.AddSingleton<INotificationService, BlueStar.Infrastructure.Services.NotificationService>();
        services.AddSingleton<IBackgroundTaskService, BlueStar.Infrastructure.Services.BackgroundTaskService>();
        services.AddSingleton<ITagsService, BlueStar.Infrastructure.Services.TagsService>();
        services.AddSingleton<IEmulatorLifecycleService, BlueStar.Infrastructure.Services.EmulatorLifecycleService>();
        services.AddSingleton<ILicenseService, BlueStar.Infrastructure.Services.LicenseService>();
        services.AddSingleton<ISecureStorage, SecureStorage>();
        services.AddSingleton<IDepotBoxAuthService, BlueStar.Infrastructure.Services.DepotBoxAuthService>();
        services.AddSingleton<ICacheService, FileCacheService>();
        services.AddSingleton<IInstanceManager, InstanceManager>();
        services.AddSingleton<DepotBoxLuaParser>();

        services.AddSingleton<IDepotBoxArchiveParser, DepotBoxArchiveParser>();
        services.AddSingleton<IDlcInstaller, BlueStar.Infrastructure.Dlc.CreamInstallerService>();
        services.AddSingleton<DownloadStateManager>();
        services.AddSingleton<IDownloadProvider, BlueStar.Infrastructure.Downloader.DepotDownloaderProvider>();
        services.AddSingleton<IUpdateService, BlueStar.Infrastructure.Update.GitHubUpdateService>();
        const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        services.AddHttpClient<ICommunityStatsService, BlueStar.Infrastructure.Services.CommunityStatsService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html, */*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,es;q=0.8");
        });

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
        services.AddSingleton<SteamResponseValidator>();
        services.AddSingleton<ISearchPipeline, SearchPipeline>();

        // General HTTP Client
        services.AddHttpClient();

        // Storage, Linking, and Multi-Instance Services
        services.AddSingleton<BlueStar.Core.Storage.IWin32Linker, BlueStar.Infrastructure.Storage.Win32Linker>();
        services.AddSingleton<BlueStar.Core.Storage.IInstanceStorageManager, BlueStar.Infrastructure.Storage.InstanceStorageManager>();
        services.AddSingleton<IReFixManager, BlueStar.Infrastructure.Emulators.ReFixManager>();
        services.AddSingleton<IModLoaderProvisioner, BlueStar.Infrastructure.Mods.ModLoaderProvisioner>();
        services.AddSingleton<IUgcBridge, BlueStar.Infrastructure.Workshop.UgcBridge>();
        services.AddSingleton<IHeuristicModDispatcher, BlueStar.Infrastructure.Workshop.HeuristicModDispatcher>();
        services.AddSingleton<IWorkshopDownloader, BlueStar.Infrastructure.Workshop.WorkshopDownloader>();

        // Engine & Modding & Emulator services
        services.AddSingleton<IEngineDetector, BlueStar.Infrastructure.Engine.EngineDetector>();
        services.AddHttpClient<IBepInExService, BlueStar.Infrastructure.Mods.BepInExService>();
        services.AddHttpClient<IWorkshopService, BlueStar.Infrastructure.Workshop.WorkshopService>();
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

        // ViewModels
        services.AddSingleton<DownloadQueueManager>();

        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<ViewModels.HomeViewModel>();
        services.AddTransient<ViewModels.LibraryViewModel>();
        services.AddTransient<ViewModels.InstanceDetailViewModel>();
        // Explore is resolved once and kept: see ISharedViewModel.
        services.AddSingleton<ViewModels.BrowseViewModel>();
        services.AddTransient<ViewModels.DownloadsViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.AboutViewModel>();
        services.AddTransient<ViewModels.DebugConsoleViewModel>();
        Services = services.BuildServiceProvider();

        // Attach runtime dependencies to DebugLogService for enriched diagnostics
        debugLogService.AttachServices(
            Services.GetService<BlueStar.Infrastructure.Storage.AppSettingsService>(),
            Services.GetService<ISteamStatusService>(),
            Services.GetService<IPrerequisiteService>());

        // Wire UI dispatchers
        BlueStar.Infrastructure.Services.NotificationService.UiDispatcher = action =>
        {
            if (Current?.Dispatcher is { } dispatcher)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.BeginInvoke(action);
                }
            }
            else
            {
                action();
            }
        };

        base.OnStartup(e);

        // The debug console is its own top-level window. Under the old OnLastWindowClose mode,
        // leaving it open and closing the main window left the app running with no visible UI,
        // holding the mutex, until the process was killed by hand — exactly the state seen on
        // 2026-09-11. OnMainWindowClose in App.xaml fixes that; this is the backstop for a
        // shutdown that starts but never finishes.
        if (MainWindow != null)
        {
            MainWindow.Closed += (_, _) => ArmShutdownWatchdog();
        }

        // Initialize dynamic localization
        try
        {
            _ = Services.GetRequiredService<ILocalizationService>();
        }
        catch { }

        // Background work — these deliberately run AFTER the window is up, so they never delay
        // the first frame.

        // Restore pending downloads from previous interrupted sessions
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000).ConfigureAwait(false);
                var queueManager = Services.GetRequiredService<DownloadQueueManager>();
                await queueManager.RestorePendingDownloadsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to restore pending downloads on startup");
            }
        });

        // Trigger background ReFix update check
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                var refixUpdater = Services.GetRequiredService<IReFixUpdateService>();
                await refixUpdater.CheckAndPerformAutoUpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background ReFix update check failed");
            }
        });
    }

    /// <summary>
    /// Guarantees the process actually ends once the main window has gone.
    /// </summary>
    /// <remarks>
    /// The UI thread is the only foreground thread here, so anything that stalls the dispatcher
    /// on the way out leaves a process with no window, still holding the single-instance mutex —
    /// which makes every later launch exit in silence. This gives shutdown a generous window and
    /// then ends the process itself, leaving a line in the log saying it had to. The watchdog is
    /// a background thread, so it can never be the thing keeping the process alive.
    /// </remarks>
    private static void ArmShutdownWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));

            try
            {
                Log.Warning("Shutdown did not finish within 10s of the main window closing. Ending the process.");
                Log.CloseAndFlush();
            }
            catch
            {
                // Nothing useful left to do if even logging is stuck.
            }

            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "BlueStar shutdown watchdog"
        };

        watchdog.Start();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("BlueStar is shutting down (exit code {ExitCode})", e.ApplicationExitCode);

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        Log.Information("Shutdown complete");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
