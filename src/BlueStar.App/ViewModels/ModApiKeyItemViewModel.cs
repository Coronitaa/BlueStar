using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// One row of the "Mod provider API keys" settings card: shows which key is active for a provider
/// and lets the user save, test or clear a personal override (same flow as the DepotBox key).
/// </summary>
public partial class ModApiKeyItemViewModel : ObservableObject
{
    private readonly IModApiKeyService _keyService;
    private readonly ILocalizationService _localization;
    private readonly INotificationService _notifications;
    private readonly ILogger _logger;

    public ModApiProvider Provider { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string PortalUrl { get; }

    /// <summary>Whether the provider still works without a key (Thunderstore's read API is public).</summary>
    public bool IsKeyOptional => Provider == ModApiProvider.Thunderstore;

    /// <summary>Whether the provider uses a per-account API path that the user can override (mod.io).</summary>
    public bool SupportsCustomApiUrl => Provider == ModApiProvider.ModIo;

    [ObservableProperty]
    private string _keyText = string.Empty;

    [ObservableProperty]
    private string _apiUrlText = string.Empty;

    [ObservableProperty]
    private string _activeApiUrl = string.Empty;

    [ObservableProperty]
    private bool _hasCustomKey;

    [ObservableProperty]
    private ModApiKeySource _keySource;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    public bool CanTest => !IsTesting;

    public ModApiKeyItemViewModel(
        ModApiProvider provider,
        string displayName,
        string descriptionKey,
        string portalUrl,
        IModApiKeyService keyService,
        ILocalizationService localization,
        INotificationService notifications,
        ILogger logger)
    {
        Provider = provider;
        DisplayName = displayName;
        PortalUrl = portalUrl;
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Description = _localization.GetString(descriptionKey, string.Empty);
    }

    /// <summary>Loads the saved override and works out which key is active.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var customKey = await _keyService.GetCustomKeyAsync(Provider, CancellationToken.None).ConfigureAwait(true);
            KeyText = customKey ?? string.Empty;
            if (SupportsCustomApiUrl)
            {
                ApiUrlText = await _keyService.GetCustomApiBaseUrlAsync(Provider, CancellationToken.None).ConfigureAwait(true) ?? string.Empty;
            }

            await RefreshSourceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {Provider} API key settings", Provider);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (SupportsCustomApiUrl)
            {
                await _keyService.SetCustomApiBaseUrlAsync(Provider, ApiUrlText, CancellationToken.None).ConfigureAwait(true);
            }

            await _keyService.SetCustomKeyAsync(Provider, KeyText, CancellationToken.None).ConfigureAwait(true);
            await RefreshSourceAsync().ConfigureAwait(true);
            TestResult = HasCustomKey
                ? T("String_ModApiKeySaved", "✅ Personal key saved. It now overrides the built-in key.")
                : T("String_ModApiKeyCleared", "ℹ Personal key removed. Using the built-in key.");
        }
        catch (ArgumentException)
        {
            TestResult = T("String_ModApiUrlInvalid", "❌ The API URL must be https and belong to the provider (e.g. https://u-12345.modapi.io/v1).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {Provider} API key", Provider);
            TestResult = string.Format(T("String_ModApiKeyNetworkError", "❌ Could not complete the request: {0}"), ex.Message);
            _notifications.ShowError(DisplayName, ex.Message);
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        try
        {
            KeyText = string.Empty;
            ApiUrlText = string.Empty;
            await _keyService.ClearCustomKeyAsync(Provider, CancellationToken.None).ConfigureAwait(true);
            await RefreshSourceAsync().ConfigureAwait(true);
            TestResult = T("String_ModApiKeyCleared", "ℹ Personal key removed. Using the built-in key.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear {Provider} API key", Provider);
        }
    }

    /// <summary>Tests the key typed in the box, or the active key when the box is empty. Nothing is saved.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = null;

        try
        {
            var result = await _keyService.ValidateKeyAsync(Provider, KeyText, CancellationToken.None).ConfigureAwait(true);
            TestResult = DescribeResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test {Provider} API key", Provider);
            TestResult = string.Format(T("String_ModApiKeyNetworkError", "❌ Could not complete the request: {0}"), ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void OpenPortal()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PortalUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open {Provider} API key page", Provider);
        }
    }

    private async Task RefreshSourceAsync()
    {
        KeySource = await _keyService.GetKeySourceAsync(Provider, CancellationToken.None).ConfigureAwait(true);
        HasCustomKey = KeySource == ModApiKeySource.CustomOverride;
        if (SupportsCustomApiUrl)
        {
            ActiveApiUrl = await _keyService.GetApiBaseUrlAsync(Provider, CancellationToken.None).ConfigureAwait(true);
        }

        StatusMessage = KeySource switch
        {
            ModApiKeySource.CustomOverride => T("String_ModApiKeyStatusCustom", "🔑 Using your personal key."),
            ModApiKeySource.BuiltIn => T("String_ModApiKeyStatusBuiltIn", "🌐 Using BlueStar's built-in key."),
            _ when IsKeyOptional => T("String_ModApiKeyStatusOptional", "🌐 No key: the public API still works; a key only unlocks account features."),
            _ => T("String_ModApiKeyStatusNone", "⚠ No key: this provider will not appear in the Mods tab.")
        };
    }

    private string DescribeResult(ModApiKeyCheckResult result) => result.Status switch
    {
        ModApiKeyCheckStatus.Valid => T("String_ModApiKeyValid", "✅ Key is valid."),
        ModApiKeyCheckStatus.Invalid => string.Format(T("String_ModApiKeyInvalid", "❌ Invalid or revoked key (HTTP {0})."), result.HttpStatusCode),
        ModApiKeyCheckStatus.RateLimited => T("String_ModApiKeyRateLimited", "⏳ The key is saturated (HTTP 429). Save your own personal key to keep going."),
        ModApiKeyCheckStatus.MissingKey => T("String_ModApiKeyMissing", "⚠ There is no key to test."),
        _ => string.Format(T("String_ModApiKeyNetworkError", "❌ Could not complete the request: {0}"),
            result.Detail ?? result.HttpStatusCode?.ToString() ?? "?")
    };

    private string T(string key, string fallback) => _localization.GetString(key, fallback);
}
