using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure;
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
    private static CancellationTokenSource? _ipcTokenSource;
    internal const string IpcPipeName = "BlueStar_Nxm_Ipc";

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
    public static IServiceProvider Services { get; internal set; } = null!;

    /// <summary>
    /// Sets the application service provider for unit testing.
    /// </summary>
    public static void SetTestServices(IServiceProvider services) => Services = services;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexId, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            var nxmArg = e.Args.FirstOrDefault(a =>
                a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("vortex://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("nxm-protocol://", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(nxmArg))
            {
                try
                {
                    using var client = new System.IO.Pipes.NamedPipeClientStream(".", IpcPipeName, System.IO.Pipes.PipeDirection.Out);
                    client.Connect(1500);
                    using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine(nxmArg);
                }
                catch
                {
                    // Ignore pipe connect failure
                }
            }

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


        // Suppress default context menus on scrollbars, thumbs, and scroll controls
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Primitives.ScrollBar),
            FrameworkElement.ContextMenuOpeningEvent,
            new System.Windows.Controls.ContextMenuEventHandler((_, args) => args.Handled = true));
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Primitives.Thumb),
            FrameworkElement.ContextMenuOpeningEvent,
            new System.Windows.Controls.ContextMenuEventHandler((_, args) => args.Handled = true));
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Primitives.RepeatButton),
            FrameworkElement.ContextMenuOpeningEvent,
            new System.Windows.Controls.ContextMenuEventHandler((_, args) => args.Handled = true));

        // DepotDownloaderMod runs on BlueStar's own runtime through Program.Main, so no system .NET is needed
        BlueStar.Infrastructure.Workshop.DepotDownloaderHost.HostExecutablePath = Environment.ProcessPath;

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

        services.AddSingleton<ILocalizationService, BlueStar.App.Services.LocalizationService>();
        services.AddBlueStar();

        // ViewModels
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

        // Games imported before depot keys were collected still have their DepotBox archive on disk; store its keys
        // (including the app-level one that decrypts Workshop content) without slowing down startup.
        var depotKeyImporter = Services.GetService<BlueStar.Infrastructure.DepotBox.DepotBoxKeyImporter>();
        var depotLuaParser = Services.GetService<DepotBoxLuaParser>();
        if (depotKeyImporter != null && depotLuaParser != null)
        {
            var archivesDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "archives");
            _ = Task.Run(async () =>
            {
                try
                {
                    await depotKeyImporter.ImportFolderAsync(archivesDir, depotLuaParser).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not collect depot keys from stored DepotBox archives");
                }
            });
        }

        // Attach runtime dependencies to DebugLogService for enriched diagnostics
        debugLogService.AttachServices(
            Services.GetService<BlueStar.Infrastructure.Storage.AppSettingsService>(),
            Services.GetService<ISteamStatusService>(),
            Services.GetService<IPrerequisiteService>());

        // Wire artwork and shortcut platform services
        BlueStar.Core.Helpers.InstanceArtworkService.ExecutableIconExtractor = ViewModels.InstanceDetailViewModel.ExtractIconFromExecutable;
        BlueStar.Core.Helpers.InstanceArtworkService.ImageToIcoConverter = Helpers.IconConversionHelper.ConvertToWindowsIco;
        BlueStar.Core.Helpers.ShortcutHelper.ImageToIcoConverter = Helpers.IconConversionHelper.ConvertToWindowsIco;

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

        // Wire notification dismiss coordinator to allow WPF exit transitions (0.18s in MainWindow.xaml) before collection purge
        BlueStar.Infrastructure.Services.NotificationService.DismissCoordinator = (item, onDismissed) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    onDismissed();
                }
                catch { }
            });
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

        // Start IPC listener for nxm:// protocol messages
        StartNxmIpcListener();

        // Register nxm:// protocol handler in Windows Registry
        try
        {
            var nexusService = Services.GetService<INexusModsService>();
            nexusService?.RegisterProtocolHandler();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register nxm:// protocol handler");
        }

        // Handle cold start NXM argument if present
        var coldNxmArg = e.Args.FirstOrDefault(a =>
            a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("vortex://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("nxm-protocol://", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(coldNxmArg))
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    if (MainWindow?.DataContext is ViewModels.MainViewModel mainVm)
                    {
                        await mainVm.HandleNxmUrlAsync(coldNxmArg);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to process cold start NXM argument");
                }
            });
        }

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

        // Background check for updated catalog snapshot
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);
                var snapshotService = Services.GetService<ICatalogSnapshotService>();
                if (snapshotService != null)
                {
                    var settings = Services.GetService<BlueStar.Infrastructure.Storage.AppSettingsService>();
                    var manifestUrl = settings?.CatalogManifestUrl;
                    if (!string.IsNullOrWhiteSpace(manifestUrl))
                    {
                        await snapshotService.CheckAndUpdateSnapshotAsync(manifestUrl).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Background catalog snapshot check skipped or failed");
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

    /// <summary>A headless --launch session changed playtime/state on disk; re-saving raises InstancesChanged so the views reload.</summary>
    private static async Task RefreshInstanceAsync(string id)
    {
        if (!Guid.TryParse(id, out var instanceId)) return;
        var manager = Services.GetRequiredService<IInstanceManager>();
        if (await manager.GetByIdAsync(instanceId).ConfigureAwait(false) is { } instance)
            await manager.UpdateAsync(instance).ConfigureAwait(false);
    }

    private void StartNxmIpcListener()
    {
        _ipcTokenSource = new CancellationTokenSource();
        var token = _ipcTokenSource.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        IpcPipeName,
                        System.IO.Pipes.PipeDirection.In,
                        1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new System.IO.StreamReader(server);
                    var nxmUrl = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (nxmUrl != null && nxmUrl.StartsWith(BlueStar.Infrastructure.Launcher.HeadlessLaunch.InstanceChangedMessage, StringComparison.Ordinal))
                    {
                        await RefreshInstanceAsync(nxmUrl[BlueStar.Infrastructure.Launcher.HeadlessLaunch.InstanceChangedMessage.Length..]).ConfigureAwait(false);
                    }
                    else if (!string.IsNullOrWhiteSpace(nxmUrl))
                    {
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (MainWindow != null)
                                {
                                    if (MainWindow.WindowState == WindowState.Minimized)
                                    {
                                        MainWindow.WindowState = WindowState.Normal;
                                    }
                                    MainWindow.Activate();
                                    MainWindow.Focus();

                                    if (MainWindow.DataContext is ViewModels.MainViewModel mainVm)
                                    {
                                        _ = mainVm.HandleNxmUrlAsync(nxmUrl);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Failed to handle incoming IPC NXM url");
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error in NXM IPC server loop");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("BlueStar is shutting down (exit code {ExitCode})", e.ApplicationExitCode);

        _ipcTokenSource?.Cancel();
        _ipcTokenSource?.Dispose();

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
