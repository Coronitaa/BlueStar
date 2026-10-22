using System;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Services;

/// <summary>
/// Verifies availability of the Microsoft Edge WebView2 Evergreen Runtime and handles recovery workflows.
/// </summary>
public static class WebView2AvailabilityService
{
    private static bool? _isCachedAvailable;

    /// <summary>
    /// Checks whether the WebView2 Evergreen Runtime is installed and usable on the host Windows system.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_isCachedAvailable.HasValue && _isCachedAvailable.Value)
            return true;

        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            bool available = !string.IsNullOrWhiteSpace(version);
            _isCachedAvailable = available;
            return available;
        }
        catch
        {
            _isCachedAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Returns the installed WebView2 version string, or null if not available.
    /// </summary>
    public static string? GetInstalledVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the official Microsoft Edge WebView2 Evergreen Runtime installer download page in the default system browser.
    /// </summary>
    public static void OpenOfficialDownloadPage()
    {
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
