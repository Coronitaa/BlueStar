using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for application settings with authentication hierarchy and storage management.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IDepotBoxAuthService _authService;
    private readonly IDepotBoxApiClient _apiClient;
    private readonly ILicenseService _licenseService;
    private readonly INotificationService _notificationService;
    private readonly AppSettingsService _appSettings;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IDebugLogService _debugLogService;

    public Action<string>? OnNavigateRequested { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<string> LanguageOptions { get; } =
    [
        "English",
        "Español (Latinoamérica)"
    ];

    [ObservableProperty]
    private string _selectedLanguageOption = "English";

    partial void OnSelectedLanguageOptionChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var targetCode = value.Contains("Español", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        if (_localizationService.CurrentLanguage != targetCode)
        {
            _localizationService.SetLanguage(targetCode);
        }
    }

    [ObservableProperty]
    private string _appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.4.0";

    [ObservableProperty]
    private string _defaultApiUrl = "https://depotbox.org";

    [ObservableProperty]
    private string _defaultApiKey = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _hasCustomKey;

    [ObservableProperty]
    private string _authStatusMessage = string.Empty;

    [ObservableProperty]
    private string? _connectionTestResult;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    private string _instanceRoot = string.Empty;

    [ObservableProperty]
    private string _defaultDownloadDirectory = string.Empty;

    partial void OnDefaultDownloadDirectoryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = _appSettings.SetDefaultDownloadDirectoryAsync(value);
        }
    }

    [ObservableProperty]
    private bool _deleteDepotsAfterInstall = true;

    partial void OnDeleteDepotsAfterInstallChanged(bool value)
    {
        _ = _appSettings.SetDeleteDepotsAfterInstallAsync(value);
    }

    [ObservableProperty]
    private bool _showNsfwContent;

    partial void OnShowNsfwContentChanged(bool value)
    {
        _ = _appSettings.SetShowNsfwContentAsync(value);
    }

    [ObservableProperty]
    private bool _showDrmContent = true;

    partial void OnShowDrmContentChanged(bool value)
    {
        _ = _appSettings.SetShowDrmContentAsync(value);
    }

    [ObservableProperty]
    private bool _enableExperimentalMods;

    partial void OnEnableExperimentalModsChanged(bool value)
    {
        _ = _appSettings.SetEnableExperimentalModsAsync(value);
    }

    [ObservableProperty]
    private bool _checkSystemRequirementsOnStartup = true;

    partial void OnCheckSystemRequirementsOnStartupChanged(bool value)
    {
        _ = _appSettings.SetCheckSystemRequirementsOnStartupAsync(value);
    }

    [ObservableProperty]
    private bool _enableDebugSystem;

    partial void OnEnableDebugSystemChanged(bool value)
    {
        _ = _appSettings.SetEnableDebugSystemAsync(value);
    }

    [ObservableProperty]
    private string? _diagnosticStatusMessage;

    [ObservableProperty]
    private string? _lastDiagnosticFilePath;

    [ObservableProperty]
    private bool _isGeneratingReport;

    // ── System Prerequisites in Settings ──
    private readonly IPrerequisiteService? _prerequisiteService;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<PrerequisiteItem> _systemPrerequisites = [];

    [ObservableProperty]
    private bool _isScanningRequirements;

    [ObservableProperty]
    private bool _isInstallingRequirements;

    [ObservableProperty]
    private string _installRequirementsStatusText = string.Empty;

    [ObservableProperty]
    private bool _hasMissingRequirements;

    [ObservableProperty]
    private int _missingRequirementsCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    public SettingsViewModel(
        IDepotBoxAuthService authService,
        IDepotBoxApiClient apiClient,
        ILicenseService licenseService,
        INotificationService notificationService,
        AppSettingsService appSettings,
        ILocalizationService localizationService,
        ILogger<SettingsViewModel> logger,
        IPrerequisiteService? prerequisiteService = null,
        IDebugLogService? debugLogService = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _prerequisiteService = prerequisiteService;
        _debugLogService = debugLogService ?? new BlueStar.Infrastructure.Services.DebugLogService(_appSettings);

        _selectedLanguageOption = _localizationService.CurrentLanguage == "es" ? "Español (Latinoamérica)" : "English";

        DeleteDepotsAfterInstall = _appSettings.DeleteDepotsAfterInstall;
        ShowNsfwContent = _appSettings.ShowNsfwContent;
        ShowDrmContent = _appSettings.ShowDrmContent;
        EnableExperimentalMods = _appSettings.EnableExperimentalMods;
        EnableDebugSystem = _appSettings.EnableDebugSystem;
        CheckSystemRequirementsOnStartup = _appSettings.CheckSystemRequirementsOnStartup;

        DefaultApiUrl = _appSettings.DefaultApiUrl;
        DefaultApiKey = _appSettings.DefaultApiKey ?? string.Empty;
        DefaultDownloadDirectory = _appSettings.DefaultDownloadDirectory;

        InstanceRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances");

        _ = LoadSettingsAsync();
        _ = ScanRequirementsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            DefaultApiUrl = _appSettings.DefaultApiUrl;
            DefaultApiKey = _appSettings.DefaultApiKey ?? string.Empty;

            var customKey = await _authService.GetCustomApiKeyAsync(CancellationToken.None).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(customKey))
            {
                ApiKey = customKey;
                HasCustomKey = true;
                AuthStatusMessage = "🔑 Personal Key active: Overriding backend default API configuration.";
            }
            else
            {
                HasCustomKey = false;
                AuthStatusMessage = "🌐 Built-in backend API active and operational. Personal API key is optional.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings");
        }
    }

    /// <summary>
    /// Saves the Default Backend API configuration.
    /// </summary>
    [RelayCommand]
    private async Task SaveDefaultApiAsync()
    {
        try
        {
            await _appSettings.SetDefaultApiUrlAsync(DefaultApiUrl).ConfigureAwait(true);
            await _appSettings.SetDefaultApiKeyAsync(DefaultApiKey).ConfigureAwait(true);
            _notificationService.ShowSuccess("Backend API Updated", "Default API settings were saved successfully.");
            ConnectionTestResult = "✅ Default API configuration updated.";
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Configuration Error", ex.Message);
        }
    }

    /// <summary>
    /// Saves the Personal DepotBox API key override to secure storage.
    /// </summary>
    [RelayCommand]
    private async Task SaveApiKeyAsync()
    {
        try
        {
            await _authService.SetCustomApiKeyAsync(ApiKey, CancellationToken.None).ConfigureAwait(true);
            HasCustomKey = !string.IsNullOrWhiteSpace(ApiKey);
            AuthStatusMessage = HasCustomKey
                ? "🔑 Personal Key active: Overriding backend default API configuration."
                : "🌐 No personal override: Using built-in default backend API.";

            ConnectionTestResult = "✅ Personal key saved as active override.";
            _notificationService.ShowSuccess("Settings Saved", "Personal API key updated in secure storage.");
            _logger.LogInformation("DepotBox personal API key saved");
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"❌ Failed to save: {ex.Message}";
            _notificationService.ShowError("Configuration Error", $"Could not save key: {ex.Message}");
            _logger.LogError(ex, "Failed to save API key");
        }
    }

    /// <summary>
    /// Clears the personal API key override.
    /// </summary>
    [RelayCommand]
    private async Task ClearApiKeyAsync()
    {
        try
        {
            ApiKey = string.Empty;
            await _authService.ClearCustomApiKeyAsync(CancellationToken.None).ConfigureAwait(true);
            HasCustomKey = false;
            AuthStatusMessage = "🌐 Personal override removed: Using built-in default backend API.";
            ConnectionTestResult = "ℹ Personal override removed. Using built-in default API.";
            _notificationService.ShowInfo("Override Removed", "Reverted to built-in default backend API.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear API key");
        }
    }

    /// <summary>
    /// Tests the DepotBox API connection with the effective active key.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionTestResult = null;

        try
        {
            // Save key first if user typed a personal override
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _authService.SetCustomApiKeyAsync(ApiKey, CancellationToken.None).ConfigureAwait(true);
                HasCustomKey = true;
            }

            // Test with a simple availability check
            var isAvailable = await _apiClient.CheckAvailabilityAsync(730, CancellationToken.None).ConfigureAwait(true);
            ConnectionTestResult = "✅ Connection successful! DepotBox API is responsive and online.";
            _notificationService.ShowSuccess("DepotBox Connected", "Connection test to DepotBox API succeeded.");
            _logger.LogInformation("DepotBox API connection test passed");
        }
        catch (UnauthorizedAccessException)
        {
            ConnectionTestResult = "❌ Invalid API key or unauthorized access (401).";
            _notificationService.ShowError("Authentication Error", "The current API key is invalid.");
        }
        catch (HttpRequestException ex)
        {
            ConnectionTestResult = $"❌ Connection error with DepotBox backend: {ex.Message}";
            _notificationService.ShowError("Connection Error", ex.Message);
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"❌ Error: {ex.Message}";
            _notificationService.ShowError("Test Error", ex.Message);
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    /// <summary>
    /// Opens an OpenFolderDialog to select the default game downloads and installs directory.
    /// </summary>
    [RelayCommand]
    private async Task BrowseDownloadDirectoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Default Game Downloads & Installation Directory",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(DefaultDownloadDirectory) && System.IO.Directory.Exists(DefaultDownloadDirectory))
        {
            dialog.InitialDirectory = DefaultDownloadDirectory;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DefaultDownloadDirectory = dialog.FolderName;
            await _appSettings.SetDefaultDownloadDirectoryAsync(dialog.FolderName).ConfigureAwait(true);
            _notificationService.ShowSuccess("Directory Updated", $"Default download location set to:\n{dialog.FolderName}");
        }
    }

    /// <summary>
    /// Opens the default downloads folder in Windows File Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenDownloadDirectory()
    {
        try
        {
            var dir = DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(dir))
                dir = InstanceRoot;

            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open download directory in Explorer");
        }
    }

    /// <summary>
    /// Scans the system requirements.
    /// </summary>
    [RelayCommand]
    public async Task ScanRequirementsAsync()
    {
        if (_prerequisiteService == null) return;

        IsScanningRequirements = true;
        try
        {
            var items = await _prerequisiteService.DetectSystemPrerequisitesAsync(CancellationToken.None).ConfigureAwait(true);
            SystemPrerequisites = new System.Collections.ObjectModel.ObservableCollection<PrerequisiteItem>(items);

            var missing = items.Where(i => i.Status != PrerequisiteStatus.InstalledInSystem && i.Status != PrerequisiteStatus.InstalledSuccess).ToList();
            MissingRequirementsCount = missing.Count;
            HasMissingRequirements = missing.Count > 0;
        }
        catch { }
        finally
        {
            IsScanningRequirements = false;
        }
    }

    /// <summary>
    /// Installs a specific prerequisite from Settings.
    /// </summary>
    [RelayCommand]
    public async Task InstallPrerequisiteAsync(PrerequisiteItem? item)
    {
        if (item == null || _prerequisiteService == null || IsInstallingRequirements) return;

        IsInstallingRequirements = true;
        InstallRequirementsStatusText = $"Installing {item.Name}...";
        try
        {
            var progress = new Progress<string>(msg => InstallRequirementsStatusText = msg);
            var success = await _prerequisiteService.InstallPrerequisiteAsync(item, progress, CancellationToken.None).ConfigureAwait(true);
            if (success)
            {
                _notificationService.ShowSuccess("Prerequisite Installed", $"{item.Name} installed successfully.");
            }
            else
            {
                _notificationService.ShowError("Installation Incomplete", $"Could not install {item.Name}.");
            }
            await ScanRequirementsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Installation Error", ex.Message);
        }
        finally
        {
            IsInstallingRequirements = false;
            InstallRequirementsStatusText = string.Empty;
        }
    }

    /// <summary>
    /// Installs all missing prerequisites from Settings.
    /// </summary>
    [RelayCommand]
    public async Task InstallAllRequirementsAsync()
    {
        if (_prerequisiteService == null || IsInstallingRequirements || SystemPrerequisites.Count == 0) return;

        var missing = SystemPrerequisites.Where(i => i.Status != PrerequisiteStatus.InstalledInSystem && i.Status != PrerequisiteStatus.InstalledSuccess).ToList();
        if (missing.Count == 0)
        {
            _notificationService.ShowInfo("System Up to Date", "All essential requirements are already installed.");
            return;
        }

        IsInstallingRequirements = true;
        try
        {
            var progress = new Progress<string>(msg => InstallRequirementsStatusText = msg);
            int installed = await _prerequisiteService.InstallAllPrerequisitesAsync(missing, progress, CancellationToken.None).ConfigureAwait(true);
            _notificationService.ShowSuccess("System Requirements Updated", $"{installed} requirement(s) configured successfully.");
            await ScanRequirementsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Installation Error", ex.Message);
        }
        finally
        {
            IsInstallingRequirements = false;
            InstallRequirementsStatusText = string.Empty;
        }
    }

    /// <summary>
    /// Opens an external URL in the default browser.
    /// </summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open URL: {Url}", url);
        }
    }

    private static Views.DebugConsoleWindow? _activeConsoleWindow;

    /// <summary>
    /// Opens or brings to front the live Debug Console window.
    /// </summary>
    [RelayCommand]
    private void OpenDebugConsole()
    {
        try
        {
            if (_activeConsoleWindow != null && _activeConsoleWindow.IsLoaded)
            {
                if (_activeConsoleWindow.WindowState == System.Windows.WindowState.Minimized)
                {
                    _activeConsoleWindow.WindowState = System.Windows.WindowState.Normal;
                }
                _activeConsoleWindow.Activate();
                _activeConsoleWindow.Focus();
                return;
            }

            var vm = new DebugConsoleViewModel(_debugLogService, _notificationService);
            _activeConsoleWindow = new Views.DebugConsoleWindow(vm);
            _activeConsoleWindow.Closed += (s, e) => _activeConsoleWindow = null;
            _activeConsoleWindow.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open debug console window");
            _notificationService.ShowError("Console Error", $"Could not open debug console: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a structured diagnostic log report for GitHub issues and troubleshooting.
    /// </summary>
    [RelayCommand]
    private async Task GenerateDiagnosticReportAsync()
    {
        if (IsGeneratingReport) return;

        IsGeneratingReport = true;
        DiagnosticStatusMessage = "Generando log de diagnóstico estructurado...";

        try
        {
            var filePath = await _debugLogService.GenerateDiagnosticReportAsync(CancellationToken.None).ConfigureAwait(true);
            var fileName = System.IO.Path.GetFileName(filePath);

            LastDiagnosticFilePath = filePath;
            DiagnosticStatusMessage = $"✅ Log de diagnóstico generado: {fileName}";

            try
            {
                System.Windows.Clipboard.SetDataObject(filePath, true);
            }
            catch { }

            _notificationService.ShowSuccess(
                "Diagnóstico Generado",
                $"Archivo de diagnóstico listo para GitHub:\n{fileName}\n(Ruta copiada al portapapeles)");
        }
        catch (Exception ex)
        {
            DiagnosticStatusMessage = $"❌ Error: {ex.Message}";
            _notificationService.ShowError("Error al generar diagnóstico", ex.Message);
            _logger.LogError(ex, "Failed to generate diagnostic report");
        }
        finally
        {
            IsGeneratingReport = false;
        }
    }

    /// <summary>
    /// Opens the logs directory in Windows Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var path = _debugLogService.LogsDirectoryPath;
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open logs directory in Explorer");
        }
    }

    /// <summary>
    /// Copies the generated diagnostic file path to clipboard.
    /// </summary>
    [RelayCommand]
    private void CopyDiagnosticPath()
    {
        if (string.IsNullOrWhiteSpace(LastDiagnosticFilePath)) return;
        try
        {
            System.Windows.Clipboard.SetDataObject(LastDiagnosticFilePath, true);
            _notificationService.ShowInfo("Ruta Copiada", "Ruta del archivo copiada al portapapeles.");
        }
        catch { }
    }
}
