using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Handles DepotBox API credentials, providing secure obfuscated backend key storage for PRO licenses
/// and giving absolute precedence to user-provided custom API keys from Settings.
/// </summary>
public sealed class DepotBoxAuthService : IDepotBoxAuthService
{
    private const string ApiKeyStorageKey = "depotbox_api_key";
    private readonly ISecureStorage _secureStorage;
    private readonly ILicenseService _licenseService;
    private readonly BlueStar.Infrastructure.Storage.AppSettingsService? _appSettings;
    private readonly ILogger<DepotBoxAuthService> _logger;

    public DepotBoxAuthService(
        ISecureStorage secureStorage,
        ILicenseService licenseService,
        ILogger<DepotBoxAuthService> logger,
        BlueStar.Infrastructure.Storage.AppSettingsService? appSettings = null)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appSettings = appSettings;
    }

    /// <inheritdoc />
    public async Task<string?> GetEffectiveApiKeyAsync(CancellationToken ct = default)
    {
        // 1. Personal API key override from Settings ALWAYS takes highest precedence
        var customKey = await _secureStorage.GetAsync(ApiKeyStorageKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(customKey))
        {
            _logger.LogDebug("Using personal user DepotBox API key override from secure storage");
            return customKey.Trim();
        }

        // 2. Custom backend API key configured in backend settings
        if (!string.IsNullOrWhiteSpace(_appSettings?.DefaultApiKey))
        {
            _logger.LogDebug("Using default backend DepotBox API key from configuration");
            return _appSettings.DefaultApiKey.Trim();
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<DepotBoxAuthSource> GetCurrentAuthSourceAsync(CancellationToken ct = default)
    {
        var customKey = await _secureStorage.GetAsync(ApiKeyStorageKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(customKey))
        {
            return DepotBoxAuthSource.CustomOverride;
        }

        if (!string.IsNullOrWhiteSpace(_appSettings?.DefaultApiKey) || _licenseService.IsProLicenseActive)
        {
            return DepotBoxAuthSource.DefaultBackend;
        }

        return DepotBoxAuthSource.None;
    }

    /// <inheritdoc />
    public async Task<bool> HasCustomApiKeyAsync(CancellationToken ct = default)
    {
        var customKey = await _secureStorage.GetAsync(ApiKeyStorageKey, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(customKey);
    }

    /// <inheritdoc />
    public Task<string?> GetCustomApiKeyAsync(CancellationToken ct = default)
    {
        return _secureStorage.GetAsync(ApiKeyStorageKey, ct);
    }

    /// <inheritdoc />
    public Task SetCustomApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ClearCustomApiKeyAsync(ct);
        }

        _logger.LogInformation("Saving custom DepotBox API key to secure storage");
        return _secureStorage.SetAsync(ApiKeyStorageKey, apiKey.Trim(), ct);
    }

    /// <inheritdoc />
    public Task ClearCustomApiKeyAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Clearing custom DepotBox API key from secure storage");
        return _secureStorage.DeleteAsync(ApiKeyStorageKey, ct);
    }
}
