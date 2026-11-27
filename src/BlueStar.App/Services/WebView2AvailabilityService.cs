using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Services;

/// <summary>
/// Verifies availability of the Microsoft Edge WebView2 Runtime, manages a single shared
/// lightweight Chromium environment across all embedded browsers, and handles recovery workflows.
/// Uses the installed Evergreen Runtime when present; otherwise the Fixed Version runtime bundled
/// with the portable build ({app}\WebView2Runtime), so BlueStar runs without installing anything.
/// </summary>
public static class WebView2AvailabilityService
{
    private const string BundledRuntimeFolderName = "WebView2Runtime";
    private const string BundledInstallerName = "MicrosoftEdgeWebview2Setup.exe";
    private const string SandboxAclMarker = ".acl-granted";

    private const string LightweightChromiumArgs =
        "--disable-background-networking " +
        "--disable-component-update " +
        "--disable-domain-reliability " +
        "--disable-sync " +
        "--no-first-run " +
        "--no-default-browser-check " +
        "--enable-quic " +
        "--disk-cache-size=104857600 " +
        "--disable-features=msEdgeSmartScreen,OptimizationHints,MediaRouter,CalculateNativeWinOcclusion,Translate,InterestFeedContentSuggestions,msEdgeAccountConsistency";

    private static readonly string[] TrackerFilterPatterns =
    [
        "*://*.google-analytics.com/*",
        "*://*.googletagmanager.com/*",
        "*://*.doubleclick.net/*",
        "*://*.googlesyndication.com/*",
        "*://*.googleadservices.com/*",
        "*://*.scorecardresearch.com/*",
        "*://*.quantserve.com/*",
        "*://*.clarity.ms/*",
        "*://*.hotjar.com/*",
        "*://*.criteo.com/*",
        "*://*.adnxs.com/*",
        "*://*.pubmatic.com/*",
        "*://*.rubiconproject.com/*",
        "*://*.amazon-adsystem.com/*",
        "*://connect.facebook.net/*"
    ];

    private static readonly ConcurrentDictionary<string, Task<CoreWebView2Environment>> EnvironmentCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<CoreWebView2, object> ConfiguredCores = new();

    private static bool? _isCachedAvailable;
    private static bool _hasResolvedExecutableFolder;
    private static string? _cachedExecutableFolder;
    private static int _prewarmStarted;

    /// <summary>Shared WebView2 user-data profile directory across internal store and mod browsers.</summary>
    public static string ProfileFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlueStar", "webview");

    /// <summary>
    /// Checks whether a WebView2 Runtime (installed or bundled) is usable on this system.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_isCachedAvailable.HasValue && _isCachedAvailable.Value)
            return true;

        bool available = !string.IsNullOrWhiteSpace(GetInstalledVersion());
        _isCachedAvailable = available;
        return available;
    }

    /// <summary>
    /// Returns the WebView2 version BlueStar will use, or null if none is available.
    /// </summary>
    public static string? GetInstalledVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(GetBrowserExecutableFolder());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pre-warms the shared lightweight WebView2 environment asynchronously on the caller's STA UI thread
    /// after startup so the first embedded Store, Workshop, or Nexus browser opens without cold-start latency.
    /// Must be invoked on an STA thread because CoreWebView2Environment.CreateAsync requires STA COM.
    /// </summary>
    public static void PrewarmInBackground()
    {
        if (Interlocked.Exchange(ref _prewarmStarted, 1) != 0)
            return;

        try
        {
            if (!IsAvailable()) return;
            Directory.CreateDirectory(ProfileFolder);
            var envTask = CreateEnvironmentAsync(ProfileFolder);
            _ = envTask.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch { }
    }

    /// <summary>
    /// Returns a shared, cached WebView2 environment configured with lightweight Chromium arguments,
    /// on the installed Evergreen runtime or the bundled Fixed Version runtime when none is installed.
    /// </summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        var normalizedFolder = Path.GetFullPath(userDataFolder);
        Directory.CreateDirectory(normalizedFolder);

        return EnvironmentCache.GetOrAdd(normalizedFolder, folder =>
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = LightweightChromiumArgs
            };

            var task = CoreWebView2Environment.CreateAsync(GetBrowserExecutableFolder(), folder, options);
            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        EnvironmentCache.TryRemove(new KeyValuePair<string, Task<CoreWebView2Environment>>(folder, t));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        });
    }

    /// <summary>
    /// Applies consistent lightweight settings and native ad/tracker request filtering to a <see cref="CoreWebView2"/> instance.
    /// </summary>
    public static void ConfigureLightweightBrowser(CoreWebView2 core, bool blockTrackers = true)
    {
        if (core == null) return;

        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPinchZoomEnabled = false;

        if (!blockTrackers) return;
        if (!ConfiguredCores.TryAdd(core, new object())) return;

        try
        {
            foreach (var pattern in TrackerFilterPatterns)
            {
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
            }

            core.WebResourceRequested += (_, args) =>
            {
                try
                {
                    args.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "");
                }
                catch { }
            };
        }
        catch { }
    }

    /// <summary>
    /// Switches a hidden or idle WebView2 instance between low and normal memory usage targets.
    /// </summary>
    public static void SetLowMemoryMode(CoreWebView2? core, bool lowMemory)
    {
        if (core == null) return;
        try
        {
            core.MemoryUsageTargetLevel = lowMemory
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }

    /// <summary>
    /// Null to use the installed Evergreen Runtime; the bundled Fixed Version folder when no runtime is installed.
    /// </summary>
    private static string? GetBrowserExecutableFolder()
    {
        if (_hasResolvedExecutableFolder)
            return _cachedExecutableFolder;

        string? resolved = null;
        if (!IsEvergreenInstalled())
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, BundledRuntimeFolderName);
            if (File.Exists(Path.Combine(bundled, "msedgewebview2.exe")))
            {
                GrantSandboxAccess(bundled);
                resolved = bundled;
            }
        }

        _cachedExecutableFolder = resolved;
        _hasResolvedExecutableFolder = true;
        return resolved;
    }

    private static bool IsEvergreenInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The WebView2 renderer runs in an AppContainer sandbox that cannot read folders such as Desktop or Downloads,
    /// where portable builds are usually extracted. Grants the sandbox SIDs read/execute once (no elevation needed:
    /// the user owns the extracted folder).
    /// </summary>
    private static void GrantSandboxAccess(string runtimeFolder)
    {
        var marker = Path.Combine(runtimeFolder, SandboxAclMarker);
        if (File.Exists(marker)) return;

        try
        {
            var psi = new ProcessStartInfo("icacls.exe", $"\"{runtimeFolder}\" /grant *S-1-15-2-1:(OI)(CI)(RX) *S-1-15-2-2:(OI)(CI)(RX) /T /Q")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(60000);
            if (process?.ExitCode == 0) File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    /// <summary>
    /// Runs the bundled WebView2 Runtime installer (Evergreen bootstrapper) when present; otherwise opens the official download page.
    /// </summary>
    public static void OpenOfficialDownloadPage()
    {
        var bundledInstaller = Path.Combine(AppContext.BaseDirectory, "prerequisites", BundledInstallerName);
        if (File.Exists(bundledInstaller))
        {
            try
            {
                Process.Start(new ProcessStartInfo(bundledInstaller) { UseShellExecute = true });
                _isCachedAvailable = null;
                return;
            }
            catch { }
        }

        try
        {
            // Direct Evergreen bootstrapper link or official landing page
            const string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
