using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.DepotBox;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Metadata;
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
            try
            {
                IntPtr hWnd = FindWindow(null, "BlueStar");
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindowAsync(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
            }
            catch
            {
                // Ignore Win32 errors
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "logs", "bluestar-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
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

        // Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // Infrastructure services
        services.AddSingleton<BlueStar.Infrastructure.Storage.AppSettingsService>();
        services.AddSingleton<INotificationService, BlueStar.Infrastructure.Services.NotificationService>();
        services.AddSingleton<ILicenseService, BlueStar.Infrastructure.Services.LicenseService>();
        services.AddSingleton<ISecureStorage, SecureStorage>();
        services.AddSingleton<IDepotBoxAuthService, BlueStar.Infrastructure.Services.DepotBoxAuthService>();
        services.AddSingleton<ICacheService, FileCacheService>();
        services.AddSingleton<IInstanceManager, InstanceManager>();
        services.AddSingleton<IMetadataProvider, SteamStoreApiClient>();
        services.AddSingleton<DepotBoxLuaParser>();
        services.AddSingleton<IDepotBoxArchiveParser, DepotBoxArchiveParser>();
        services.AddSingleton<IDlcInstaller, BlueStar.Infrastructure.Dlc.CreamInstallerService>();
        services.AddSingleton<IDownloadProvider>(sp => new BlueStar.Infrastructure.Downloader.DepotDownloaderProvider(
            Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloader.exe"),
            sp.GetRequiredService<ILogger<BlueStar.Infrastructure.Downloader.DepotDownloaderProvider>>(),
            sp.GetRequiredService<BlueStar.Infrastructure.Storage.AppSettingsService>()));
        services.AddSingleton<IUpdateService, BlueStar.Infrastructure.Update.GitHubUpdateService>();
        services.AddSingleton<ICommunityStatsService, BlueStar.Infrastructure.Services.CommunityStatsService>();

        // HTTP clients
        services.AddHttpClient<IDepotBoxApiClient, DepotBoxApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://depotbox.org");
            client.Timeout = TimeSpan.FromMinutes(15); // ZIPs are built on-the-fly
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/1.0.0");
        });

        services.AddHttpClient<SteamStoreApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://store.steampowered.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/1.0.0");
        });

        // General HTTP Client
        services.AddHttpClient();

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
        services.AddSingleton<IGameLauncher, BlueStar.Infrastructure.Launcher.GameLauncherService>();

        // ViewModels
        services.AddSingleton<DownloadQueueManager>();
        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<ViewModels.HomeViewModel>();
        services.AddTransient<ViewModels.LibraryViewModel>();
        services.AddTransient<ViewModels.InstanceDetailViewModel>();
        services.AddTransient<ViewModels.BrowseViewModel>();
        services.AddTransient<ViewModels.DownloadsViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.AboutViewModel>();

        Services = services.BuildServiceProvider();

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

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
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

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
