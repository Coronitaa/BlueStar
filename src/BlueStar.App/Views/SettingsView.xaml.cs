using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;
using BlueStar.Infrastructure.Steam;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Views;

/// <summary>
/// Settings view for application configuration and Steam authentication.
/// </summary>
public partial class SettingsView : UserControl
{
    private bool _steamLoginWebViewReady;

    /// <summary>
    /// Initializes the settings view.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            if (newVm.IsSteamLoginModalOpen)
            {
                _ = InitializeAndNavigateSteamLoginAsync(newVm);
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel vm) return;

        if (e.PropertyName == nameof(SettingsViewModel.IsSteamLoginModalOpen))
        {
            if (vm.IsSteamLoginModalOpen)
            {
                _ = InitializeAndNavigateSteamLoginAsync(vm);
            }
        }
        else if (e.PropertyName == nameof(SettingsViewModel.IsSteamAuthenticated) && !vm.IsSteamAuthenticated)
        {
            _ = ClearWebViewSteamCookiesAsync();
        }
    }

    private async Task InitializeAndNavigateSteamLoginAsync(SettingsViewModel vm)
    {
        try
        {
            if (!_steamLoginWebViewReady)
            {
                var profileFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "webview");

                Directory.CreateDirectory(profileFolder);

                var environment = await CoreWebView2Environment
                    .CreateAsync(userDataFolder: profileFolder)
                    .ConfigureAwait(true);

                await SteamLoginWebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

                SteamLoginWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56);
                SteamLoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                SteamLoginWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                SteamLoginWebView.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    vm.IsSteamLoginLoading = false;
                    _ = CheckForSteamLoginSuccessAsync(vm);
                };

                SteamLoginWebView.CoreWebView2.SourceChanged += (s, args) =>
                {
                    _ = CheckForSteamLoginSuccessAsync(vm);
                };

                _steamLoginWebViewReady = true;
            }

            vm.IsSteamLoginLoading = true;
            // Navigate to official Steam login portal
            SteamLoginWebView.CoreWebView2.Navigate("https://store.steampowered.com/login/");

            // Start periodic polling while modal is open to catch live QR scanner or mobile confirmation without full navigation
            _ = StartPeriodicLoginCheckAsync(vm);
        }
        catch (Exception ex)
        {
            vm.IsSteamLoginLoading = false;
            System.Diagnostics.Debug.WriteLine($"Failed to initialize SteamLoginWebView: {ex.Message}");
        }
    }

    private async Task StartPeriodicLoginCheckAsync(SettingsViewModel vm)
    {
        while (vm.IsSteamLoginModalOpen)
        {
            await Task.Delay(1500);
            if (!vm.IsSteamLoginModalOpen) break;
            var success = await CheckForSteamLoginSuccessAsync(vm);
            if (success) break;
        }
    }

    private async Task<bool> CheckForSteamLoginSuccessAsync(SettingsViewModel vm)
    {
        if (!vm.IsSteamLoginModalOpen || SteamLoginWebView?.CoreWebView2 == null) return false;

        try
        {
            var cookies = await SteamLoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://store.steampowered.com");
            CoreWebView2Cookie? loginSecureCookie = null;
            var cookieDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cookie in cookies)
            {
                cookieDict[cookie.Name] = cookie.Value;
                if (cookie.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(cookie.Value) &&
                    !cookie.Value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    loginSecureCookie = cookie;
                }
            }

            if (loginSecureCookie != null)
            {
                // Register session in volatile memory override
                SteamSessionCookieProvider.SetInMemorySessionCookies(cookieDict);

                var steamId64 = ExtractSteamId64(loginSecureCookie.Value);
                if (!string.IsNullOrWhiteSpace(steamId64))
                {
                    vm.OnSteamLoginSuccessful(steamId64, null);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error inspecting Steam login cookies: {ex.Message}");
        }

        return false;
    }

    private async Task ClearWebViewSteamCookiesAsync()
    {
        if (_steamLoginWebViewReady && SteamLoginWebView?.CoreWebView2 != null)
        {
            try
            {
                var cookies = await SteamLoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://store.steampowered.com");
                foreach (var c in cookies)
                {
                    SteamLoginWebView.CoreWebView2.CookieManager.DeleteCookie(c);
                }
            }
            catch { }
        }
    }

    private static string? ExtractSteamId64(string cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue)) return null;

        try
        {
            var decoded = Uri.UnescapeDataString(cookieValue);
            var sepIdx = decoded.IndexOf("||", StringComparison.Ordinal);
            if (sepIdx > 0)
            {
                var candidate = decoded.Substring(0, sepIdx).Trim();
                if (ulong.TryParse(candidate, out var id) && id > 76561197960265728UL)
                {
                    return candidate;
                }
            }
            else if (decoded.Length >= 17 && ulong.TryParse(decoded.Substring(0, 17), out var id) && id > 76561197960265728UL)
            {
                return id.ToString();
            }
        }
        catch { }

        return null;
    }
}
