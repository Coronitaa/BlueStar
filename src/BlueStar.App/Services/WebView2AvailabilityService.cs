using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Services;

/// <summary>
/// Verifies availability of the Microsoft Edge WebView2 Runtime and handles recovery workflows.
/// Uses the installed Evergreen Runtime when present; otherwise the Fixed Version runtime bundled
/// with the portable build ({app}\WebView2Runtime), so BlueStar runs without installing anything.
/// </summary>
public static class WebView2AvailabilityService
{
    private const string BundledRuntimeFolderName = "WebView2Runtime";
    private const string BundledInstallerName = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    private const string SandboxAclMarker = ".acl-granted";

    private static bool? _isCachedAvailable;

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
    /// Creates a WebView2 environment on the installed runtime, or on the bundled Fixed Version runtime when none is installed.
    /// </summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        return CoreWebView2Environment.CreateAsync(GetBrowserExecutableFolder(), userDataFolder);
    }

    /// <summary>
    /// Null to use the installed Evergreen Runtime; the bundled Fixed Version folder when no runtime is installed.
    /// </summary>
    private static string? GetBrowserExecutableFolder()
    {
        if (IsEvergreenInstalled()) return null;

        var bundled = Path.Combine(AppContext.BaseDirectory, BundledRuntimeFolderName);
        if (!File.Exists(Path.Combine(bundled, "msedgewebview2.exe"))) return null;

        GrantSandboxAccess(bundled);
        return bundled;
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
    /// Runs the bundled WebView2 Runtime offline installer when present; otherwise opens the official download page.
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
