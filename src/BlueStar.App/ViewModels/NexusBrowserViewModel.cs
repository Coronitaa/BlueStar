using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// State and logic of the Nexus Mods browser panel.
/// Uses the web nxm:// protocol flow so free / non-premium accounts can download and install
/// mods seamlessly through the official installation pipeline.
/// </summary>
public sealed partial class NexusBrowserViewModel : ObservableObject
{
    private readonly INexusModsService _nexusService;
    private readonly Func<ModPackage, Task<ModInstallResult?>> _installPackage;
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;

    private GameInstance? _instance;

    public NexusBrowserViewModel(
        INexusModsService nexusService,
        Func<ModPackage, Task<ModInstallResult?>> installPackage,
        ILocalizationService? localization,
        ILogger logger)
    {
        _nexusService = nexusService ?? throw new ArgumentNullException(nameof(nexusService));
        _installPackage = installPackage ?? throw new ArgumentNullException(nameof(installPackage));
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    private string? _gameDomain;

    [ObservableProperty]
    private string? _startUrl;

    [ObservableProperty]
    private string? _currentUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadManual))]
    private string _manualInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadManual))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isInstructionsExpanded;

    [ObservableProperty]
    private bool _isLoadingPage;

    public bool CanDownloadManual => !IsDownloading && !string.IsNullOrWhiteSpace(ManualInput) && _nexusService.CanHandleNxmUrl(ManualInput);

    public Func<Task>? OnModInstalledCallback { get; set; }

    public Action? NavigateHomeAction { get; set; }

    public Action<string>? NavigateUrlAction { get; set; }

    [RelayCommand]
    public void NavigateHome()
    {
        NavigateHomeAction?.Invoke();
    }

    public void Open(GameInstance instance, string gameDomain)
    {
        _instance = instance;
        GameDomain = gameDomain;
        StartUrl = $"https://www.nexusmods.com/{gameDomain}/mods";
        CurrentUrl = StartUrl;
        ManualInput = string.Empty;
        StatusMessage = null;
        IsStatusError = false;
        DownloadProgress = 0;
        IsDownloading = false;
        IsInstructionsExpanded = false;
    }

    public void OnDownloadStarted(string fileName)
    {
        IsDownloading = true;
        DownloadProgress = 0;
        ReportStatus(string.Format(T("String_NexusDownloadingFile", "⏳ Descargando {0} desde Nexus Mods..."), fileName), isError: false);
    }

    public void UpdateDownloadProgress(double progress)
    {
        DownloadProgress = Math.Clamp(progress, 0, 100);
    }

    public async Task<bool> HandleDownloadedFileAsync(string localFilePath, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath) || _instance == null)
        {
            IsDownloading = false;
            return false;
        }

        IsDownloading = true;
        DownloadProgress = 100;

        try
        {
            var fileName = Path.GetFileName(localFilePath);
            var baseName = Path.GetFileNameWithoutExtension(localFilePath);
            if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                baseName = Path.GetFileNameWithoutExtension(baseName);
            }

            // Try to enrich metadata if current page or sourceUrl points to a known mod
            NexusModInfo? modDetails = null;
            long? modId = TryExtractModId(CurrentUrl) ?? TryExtractModId(sourceUrl);

            if (modId.HasValue && !string.IsNullOrWhiteSpace(GameDomain))
            {
                try
                {
                    modDetails = await _nexusService.GetModDetailsAsync(GameDomain, modId.Value).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not fetch details for mod ID {ModId}", modId.Value);
                }
            }

            var cleanName = baseName.Replace('_', ' ').Replace('-', ' ').Trim();
            var modName = !string.IsNullOrWhiteSpace(modDetails?.Name) ? modDetails.Name : cleanName;
            if (string.IsNullOrWhiteSpace(modName))
            {
                modName = "Nexus Mod";
            }

            var package = new ModPackage
            {
                Path = localFilePath,
                Name = modName,
                Source = new ModSourceRef
                {
                    Provider = "nexus",
                    RemoteId = modId.HasValue ? $"{GameDomain}:{modId.Value}:{baseName}" : $"{GameDomain}:{baseName}",
                    PageUrl = modId.HasValue ? $"https://www.nexusmods.com/{GameDomain}/mods/{modId.Value}" : (CurrentUrl ?? $"https://www.nexusmods.com/{GameDomain}/mods"),
                    IconUrl = modDetails?.PictureUrl
                }
            };

            ReportStatus(string.Format(T("String_ModInstalling", "📦 Instalando {0}..."), package.Name), isError: false);
            var result = await _installPackage(package).ConfigureAwait(true);

            if (result?.Status == ModInstallStatus.Installed)
            {
                ReportStatus(string.Format(T("String_ModInstalled", "✅ {0} instalado ({1} archivos)."), package.Name, result.Record?.Files.Count ?? 0), isError: false);
                if (OnModInstalledCallback != null)
                {
                    await OnModInstalledCallback().ConfigureAwait(true);
                }
                return true;
            }
            else if (result?.Status == ModInstallStatus.NeedsConfirmation)
            {
                return false;
            }
            else
            {
                ReportStatus(result?.Error ?? T("String_ModInstallFailedGeneric", "❌ Error al instalar mod."), isError: true);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process downloaded file: {Path}", localFilePath);
            ReportStatus(string.Format(T("String_NexusDownloadFailedDetail", "❌ Error al procesar descarga: {0}"), ex.Message), isError: true);
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private static long? TryExtractModId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = Regex.Match(url, @"/mods/(?<id>\d+)", RegexOptions.IgnoreCase);
        if (match.Success && long.TryParse(match.Groups["id"].Value, out var id))
        {
            return id;
        }
        return null;
    }

    public void Close()
    {
        // View cleanup if any
    }

    public void OnNavigated(string? url)
    {
        CurrentUrl = url;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadManual))]
    public async Task DownloadManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualInput)) return;
        await HandleNxmUrlAsync(ManualInput.Trim()).ConfigureAwait(true);
    }

    [RelayCommand]
    public void OpenInBrowser()
    {
        var target = !string.IsNullOrWhiteSpace(CurrentUrl) ? CurrentUrl : StartUrl;
        if (!string.IsNullOrWhiteSpace(target))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to launch external browser for {Url}", target);
            }
        }
    }

    /// <summary>
    /// Processes an nxm:// URL, downloads the mod using the key and expires tokens,
    /// and installs it through the shared mod installation pipeline.
    /// </summary>
    public async Task<bool> HandleNxmUrlAsync(string nxmUrl)
    {
        if (string.IsNullOrWhiteSpace(nxmUrl) || _instance == null) return false;

        if (!_nexusService.CanHandleNxmUrl(nxmUrl))
        {
            ReportStatus("El enlace proporcionado no es un enlace nxm:// válido de Nexus Mods.", isError: true);
            return false;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        ReportStatus(T("String_NexusDownloading", "⏳ Descargando mod desde Nexus Mods..."), isError: false);

        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p);
            var package = await _nexusService.DownloadFromNxmAsync(_instance, nxmUrl, progress).ConfigureAwait(true);

            if (package == null)
            {
                if (TryFallbackWebDownload(nxmUrl))
                {
                    return true;
                }

                ReportStatus(T("String_NexusDownloadFailed", "❌ Error al descargar el mod desde Nexus Mods."), isError: true);
                return false;
            }

            ReportStatus(string.Format(T("String_ModInstalling", "📦 Instalando {0}..."), package.Name), isError: false);
            var result = await _installPackage(package).ConfigureAwait(true);

            if (result?.Status == ModInstallStatus.Installed)
            {
                ReportStatus(string.Format(T("String_ModInstalled", "✅ {0} instalado ({1} archivos)."), package.Name, result.Record?.Files.Count ?? 0), isError: false);
                ManualInput = string.Empty;

                if (OnModInstalledCallback != null)
                {
                    await OnModInstalledCallback().ConfigureAwait(true);
                }
                return true;
            }
            else if (result?.Status == ModInstallStatus.NeedsConfirmation)
            {
                // Confirmation dialog handled by tab
                return false;
            }
            else
            {
                ReportStatus(result?.Error ?? T("String_ModInstallFailedGeneric", "❌ Error al instalar mod."), isError: true);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process nxm download: {Url}", nxmUrl);

            if (TryFallbackWebDownload(nxmUrl))
            {
                return true;
            }

            ReportStatus(string.Format(T("String_NexusDownloadFailedDetail", "❌ Error al descargar desde Nexus: {0}"), ex.Message), isError: true);
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private bool TryFallbackWebDownload(string nxmUrl)
    {
        if (NavigateUrlAction == null) return false;

        var match = Regex.Match(nxmUrl, @"(?:nxm|vortex|nxm-protocol)://(?<game>[^/?#]+)/mods/(?<mod>\d+)/files/(?<file>\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var game = match.Groups["game"].Value;
            var mod = match.Groups["mod"].Value;
            var file = match.Groups["file"].Value;
            var target = $"https://www.nexusmods.com/{game}/mods/{mod}?tab=files&file_id={file}&nmm=0";
            ReportStatus(T("String_NexusRedirectingDownload", "Preparando descarga compatible con Vortex..."), isError: false);
            NavigateUrlAction(target);
            return true;
        }
        return false;
    }

    public void ReportStatus(string? message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
}
