using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Views;

/// <summary>
/// Embedded Nexus Mods browser with automatic nxm:// and manual file download interception,
/// and secure navigation boundary restricted to the game's mod subsite and login providers.
/// </summary>
public partial class NexusBrowserPanel : UserControl
{
    private NexusBrowserViewModel? _viewModel;
    private Task? _webViewInit;

    public NexusBrowserPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.NavigateHomeAction = null;
            _viewModel.NavigateUrlAction = null;
        }
        _viewModel = e.NewValue as NexusBrowserViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.NavigateHomeAction = () =>
            {
                var homeUrl = GetGameModsHomeUrl(_viewModel?.GameDomain);
                if (!string.IsNullOrWhiteSpace(homeUrl) && NexusWebView.CoreWebView2 != null)
                {
                    NexusWebView.CoreWebView2.Navigate(homeUrl);
                }
            };
            _viewModel.NavigateUrlAction = url =>
            {
                if (!string.IsNullOrWhiteSpace(url) && NexusWebView.CoreWebView2 != null)
                {
                    NexusWebView.CoreWebView2.Navigate(url);
                }
            };
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NexusBrowserViewModel.StartUrl) && IsVisible)
        {
            _ = NavigateAsync(_viewModel?.StartUrl);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _ = NavigateAsync(_viewModel?.StartUrl);
        }
    }

    private async Task NavigateAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            if (!BlueStar.App.Services.WebView2AvailabilityService.IsAvailable())
            {
                ShowFallback();
                return;
            }

            _webViewInit ??= InitializeWebViewAsync();
            await _webViewInit.ConfigureAwait(true);

            if (NexusWebView.CoreWebView2 != null &&
                !string.Equals(NexusWebView.CoreWebView2.Source, url, StringComparison.OrdinalIgnoreCase))
            {
                NexusWebView.CoreWebView2.Navigate(url);
            }
        }
        catch (Exception)
        {
            _webViewInit = null;
            ShowFallback();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "webview");
        Directory.CreateDirectory(profileFolder);

        var environment = await BlueStar.App.Services.WebView2AvailabilityService
            .CreateEnvironmentAsync(profileFolder)
            .ConfigureAwait(true);
        await NexusWebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        NexusWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56);
        var core = NexusWebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.DownloadStarting += OnDownloadStarting;

        core.NavigationStarting += (_, args) =>
        {
            if (_viewModel != null) _viewModel.IsLoadingPage = true;

            if (string.IsNullOrWhiteSpace(args.Uri)) return;

            // Automatically rewrite Vortex Mod Manager Download URLs (&nmm=1) to manual download URLs (&nmm=0)
            // so files download cleanly via CDN in the embedded browser without needing an external Vortex or Premium API key
            if (args.Uri.Contains("&nmm=1", StringComparison.OrdinalIgnoreCase) ||
                args.Uri.Contains("?nmm=1", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                var manualUrl = args.Uri.Replace("&nmm=1", "&nmm=0", StringComparison.OrdinalIgnoreCase)
                                        .Replace("?nmm=1", "?nmm=0", StringComparison.OrdinalIgnoreCase);
                core.Navigate(manualUrl);
                return;
            }

            if (IsNxmOrVortexScheme(args.Uri))
            {
                args.Cancel = true;
                if (_viewModel != null) _viewModel.IsLoadingPage = false;
                HandleNxmProtocolInBrowser(args.Uri, core);
                return;
            }

            if (!IsAllowedNexusUrl(args.Uri, _viewModel?.GameDomain))
            {
                args.Cancel = true;
                if (_viewModel != null) _viewModel.IsLoadingPage = false;
                var msg = (Application.Current?.TryFindResource("String_NexusNavigationRestricted") as string)
                    ?? "Navegación externa o hacia otro juego restringida en este panel.";
                _viewModel?.ReportStatus(msg, isError: false);
            }
        };

        core.NavigationCompleted += (_, _) =>
        {
            if (_viewModel != null) _viewModel.IsLoadingPage = false;

            var currentSource = core.Source;
            if (!string.IsNullOrWhiteSpace(currentSource) &&
                currentSource.Contains("tab=files", StringComparison.OrdinalIgnoreCase) &&
                currentSource.Contains("file_id=", StringComparison.OrdinalIgnoreCase))
            {
                // Auto-trigger slow download button on file download countdown pages
                var script = @"
                    (function() {
                        var attempts = 0;
                        var timer = setInterval(function() {
                            var btn = document.getElementById('slowDownloadButton');
                            if (btn) {
                                clearInterval(timer);
                                btn.click();
                            } else if (++attempts > 20) {
                                clearInterval(timer);
                            }
                        }, 250);
                    })();";
                _ = core.ExecuteScriptAsync(script);
            }
        };

        core.LaunchingExternalUriScheme += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Uri)) return;

            if (IsNxmOrVortexScheme(args.Uri))
            {
                args.Cancel = true;
                HandleNxmProtocolInBrowser(args.Uri, core);
            }
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (string.IsNullOrWhiteSpace(args.Uri)) return;

            if (IsNxmOrVortexScheme(args.Uri))
            {
                HandleNxmProtocolInBrowser(args.Uri, core);
                return;
            }

            if (IsAllowedNexusUrl(args.Uri, _viewModel?.GameDomain))
            {
                core.Navigate(args.Uri);
            }
            else
            {
                _viewModel?.ReportStatus("Enlace externo no permitido en este panel. Usa 'Navegador Externo'.", isError: false);
            }
        };

        core.SourceChanged += (_, _) => _viewModel?.OnNavigated(core.Source);
    }

    private void HandleNxmProtocolInBrowser(string uri, CoreWebView2 core)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            uri,
            @"(?:nxm|vortex|nxm-protocol)://(?<game>[^/?#]+)/mods/(?<mod>\d+)/files/(?<file>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var game = match.Groups["game"].Value;
            var mod = match.Groups["mod"].Value;
            var file = match.Groups["file"].Value;
            var manualDownloadUrl = $"https://www.nexusmods.com/{game}/mods/{mod}?tab=files&file_id={file}&nmm=0";
            _viewModel?.ReportStatus("Iniciando descarga compatible con Vortex...", isError: false);
            core.Navigate(manualDownloadUrl);
        }
        else
        {
            _ = _viewModel?.HandleNxmUrlAsync(uri);
        }
    }

    private static bool IsNxmOrVortexScheme(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        return uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("vortex://", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("nxm-protocol://", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var downloadUri = args.DownloadOperation.Uri;
        var fileName = Path.GetFileName(args.ResultFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(args.DownloadOperation.ResultFilePath);
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"nexus_mod_{DateTime.UtcNow.Ticks}.zip";
        }

        // Intercept download: suppress Edge default browser flyout
        args.Handled = true;

        var tempDir = Path.Combine(Path.GetTempPath(), "BlueStar", "nexus_downloads");
        Directory.CreateDirectory(tempDir);
        var targetFile = Path.Combine(tempDir, fileName);
        if (File.Exists(targetFile))
        {
            targetFile = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        }

        args.ResultFilePath = targetFile;

        _viewModel?.OnDownloadStarted(fileName);

        args.DownloadOperation.BytesReceivedChanged += (dSender, _) =>
        {
            if (dSender is CoreWebView2DownloadOperation op)
            {
                var total = op.TotalBytesToReceive;
                if (total.HasValue && total.Value > 0)
                {
                    var progress = (double)op.BytesReceived / total.Value * 100.0;
                    _viewModel?.UpdateDownloadProgress(progress);
                }
            }
        };

        args.DownloadOperation.StateChanged += (dSender, _) =>
        {
            if (dSender is CoreWebView2DownloadOperation op)
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        if (_viewModel != null)
                        {
                            await _viewModel.HandleDownloadedFileAsync(targetFile, downloadUri).ConfigureAwait(true);
                        }
                    });
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        _viewModel?.ReportStatus($"Descarga interrumpida: {op.InterruptReason}", isError: true);
                    });
                }
            }
        };
    }

    public static bool IsAllowedNexusUrl(string? urlString, string? gameDomain)
    {
        if (string.IsNullOrWhiteSpace(urlString)) return true;

        if (urlString.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            urlString.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            urlString.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            urlString.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (urlString.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();

        // 1. Security infrastructure, DDoS protection & Bot Detection (Cloudflare, Turnstile, Captcha)
        // Cloudflare challenge platform runs on /cdn-cgi/ on any domain, or on cloudflare.com
        if (path.StartsWith("/cdn-cgi/") || path == "/cdn-cgi" ||
            host == "challenges.cloudflare.com" || host.EndsWith(".cloudflare.com") ||
            host.Contains("cloudflareinsights") || host.Contains("turnstile") ||
            host == "hcaptcha.com" || host.EndsWith(".hcaptcha.com") ||
            host == "recaptcha.net" || host.EndsWith(".recaptcha.net") ||
            (host.EndsWith("google.com") && path.Contains("recaptcha")) ||
            host == "www.gstatic.com" || host.EndsWith(".gstatic.com"))
        {
            return true;
        }

        // 2. Cookie consent & compliance managers (OneTrust, Cookiebot, CookieLaw)
        if (host.Contains("cookielaw.org") || host.Contains("onetrust.com") || host.Contains("cookiebot.com"))
        {
            return true;
        }

        // 3. External authentication / SSO identity providers
        if (host == "accounts.google.com" || host.EndsWith(".accounts.google.com") ||
            host == "discord.com" || host == "discordapp.com" || host.EndsWith(".discord.com") ||
            host == "steamcommunity.com" || host == "steampowered.com" || host.EndsWith(".steampowered.com") ||
            host == "appleid.apple.com" ||
            host == "github.com" || host.EndsWith(".github.com"))
        {
            return true;
        }

        // 4. Nexus Mods hosts
        bool isNexus = host == "nexusmods.com" || host.EndsWith(".nexusmods.com") ||
                       host == "nexus-cdn.com" || host.EndsWith(".nexus-cdn.com");
        if (!isNexus)
        {
            return false;
        }

        // Authentication & profile management
        if (host == "users.nexusmods.com" || host == "auth.nexusmods.com" ||
            path.StartsWith("/login") || path.StartsWith("/register") ||
            path.StartsWith("/users") || path.StartsWith("/oauth") ||
            path.StartsWith("/sso") || path.StartsWith("/account") || path.StartsWith("/auth"))
        {
            return true;
        }

        // Nexus CDN, static delivery, images, assets, downloads, APIs
        if (host.Contains("files") || host.Contains("download") || host.Contains("static") ||
            host.Contains("media") || host.Contains("images") || host.Contains("support") ||
            path.Contains("download") || path.StartsWith("/core/libs/common/managers/downloads") ||
            path.StartsWith("/ajax/") || path.StartsWith("/assets/") || path.StartsWith("/static/") ||
            path.StartsWith("/images/") || path.StartsWith("/content/") || path.StartsWith("/api/"))
        {
            return true;
        }

        // Game subsite verification
        if (!string.IsNullOrWhiteSpace(gameDomain))
        {
            var cleanDomain = gameDomain.Trim().ToLowerInvariant();
            var prefix1 = "/" + cleanDomain;
            var prefix2 = "/games/" + cleanDomain;

            if (path == prefix1 || path.StartsWith(prefix1 + "/") ||
                path == prefix2 || path.StartsWith(prefix2 + "/"))
            {
                return true;
            }

            return false;
        }

        return false;
    }

    public static string GetGameModsHomeUrl(string? gameDomain)
    {
        return !string.IsNullOrWhiteSpace(gameDomain)
            ? $"https://www.nexusmods.com/{gameDomain.Trim()}/mods"
            : "https://www.nexusmods.com/";
    }

    private void ShowFallback()
    {
        NexusWebView.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
    }

    private void OnInstallWebViewClicked(object sender, RoutedEventArgs e)
    {
        BlueStar.App.Services.WebView2AvailabilityService.OpenOfficialDownloadPage();
    }
}
