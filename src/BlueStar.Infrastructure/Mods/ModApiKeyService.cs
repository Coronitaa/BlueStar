using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Resolves keys and API base URLs for CurseForge, Nexus Mods, mod.io and Thunderstore. A personal
/// value saved in Settings (secure storage) always wins over the built-in one, mirroring the DepotBox override.
/// </summary>
public sealed class ModApiKeyService : IModApiKeyService
{
    private readonly HttpClient _http;
    private readonly ISecureStorage _secureStorage;
    private readonly AppSettingsService? _appSettings;
    private readonly ILogger<ModApiKeyService> _logger;

    public ModApiKeyService(
        HttpClient http,
        ISecureStorage secureStorage,
        ILogger<ModApiKeyService> logger,
        AppSettingsService? appSettings = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appSettings = appSettings;
    }

    /// <summary>Secure storage entry that holds the personal key override for a provider.</summary>
    public static string StorageKey(ModApiProvider provider) => provider switch
    {
        ModApiProvider.CurseForge => "mod_api_key_curseforge",
        ModApiProvider.NexusMods => "mod_api_key_nexusmods",
        ModApiProvider.ModIo => "mod_api_key_modio",
        ModApiProvider.Thunderstore => "mod_api_key_thunderstore",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    /// <summary>Secure storage entry that holds the personal API base URL override for a provider.</summary>
    public static string UrlStorageKey(ModApiProvider provider) => StorageKey(provider).Replace("_key_", "_url_");

    /// <summary>Public API base URL used when nothing else is configured.</summary>
    public static string DefaultApiBaseUrl(ModApiProvider provider) => provider switch
    {
        ModApiProvider.CurseForge => "https://api.curseforge.com/v1",
        ModApiProvider.NexusMods => "https://api.nexusmods.com/v1",
        ModApiProvider.ModIo => "https://api.mod.io/v1",
        ModApiProvider.Thunderstore => "https://thunderstore.io/api",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    /// <summary>
    /// True when <paramref name="url"/> is an https URL on one of the provider's own API hosts.
    /// Guards against a pasted URL sending the key to a third party.
    /// </summary>
    public static bool IsAllowedApiBaseUrl(ModApiProvider provider, string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        return provider switch
        {
            ModApiProvider.CurseForge => host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase),
            ModApiProvider.NexusMods => host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase),
            ModApiProvider.ModIo => host.Equals("api.mod.io", StringComparison.OrdinalIgnoreCase)
                                    || host.EndsWith(".modapi.io", StringComparison.OrdinalIgnoreCase),
            ModApiProvider.Thunderstore => host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<string?> GetEffectiveKeyAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        var customKey = await GetCustomKeyAsync(provider, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(customKey))
        {
            return customKey.Trim();
        }

        return _appSettings?.GetBuiltInModApiKey(provider);
    }

    /// <inheritdoc />
    public async Task<ModApiKeySource> GetKeySourceAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        var customKey = await GetCustomKeyAsync(provider, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(customKey))
        {
            return ModApiKeySource.CustomOverride;
        }

        return string.IsNullOrWhiteSpace(_appSettings?.GetBuiltInModApiKey(provider))
            ? ModApiKeySource.None
            : ModApiKeySource.BuiltIn;
    }

    /// <inheritdoc />
    public Task<string?> GetCustomKeyAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        return _secureStorage.GetAsync(StorageKey(provider), ct);
    }

    /// <inheritdoc />
    public Task SetCustomKeyAsync(ModApiProvider provider, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ClearCustomKeyAsync(provider, ct);
        }

        _logger.LogInformation("Saving personal {Provider} API key to secure storage", provider);
        return _secureStorage.SetAsync(StorageKey(provider), apiKey.Trim(), ct);
    }

    /// <inheritdoc />
    public async Task ClearCustomKeyAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        _logger.LogInformation("Clearing personal {Provider} API key from secure storage", provider);
        await _secureStorage.DeleteAsync(StorageKey(provider), ct).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(UrlStorageKey(provider), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> GetApiBaseUrlAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        var customUrl = await GetCustomApiBaseUrlAsync(provider, ct).ConfigureAwait(false);
        if (IsAllowedApiBaseUrl(provider, customUrl))
        {
            return customUrl!.Trim().TrimEnd('/');
        }

        var builtInUrl = _appSettings?.GetBuiltInModApiBaseUrl(provider);
        if (IsAllowedApiBaseUrl(provider, builtInUrl))
        {
            return builtInUrl!.Trim().TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(builtInUrl))
        {
            _logger.LogWarning("Ignoring built-in {Provider} API URL outside the provider's domains", provider);
        }

        return DefaultApiBaseUrl(provider);
    }

    /// <inheritdoc />
    public Task<string?> GetCustomApiBaseUrlAsync(ModApiProvider provider, CancellationToken ct = default)
    {
        return _secureStorage.GetAsync(UrlStorageKey(provider), ct);
    }

    /// <inheritdoc />
    public Task SetCustomApiBaseUrlAsync(ModApiProvider provider, string? baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return _secureStorage.DeleteAsync(UrlStorageKey(provider), ct);
        }

        if (!IsAllowedApiBaseUrl(provider, baseUrl))
        {
            throw new ArgumentException($"'{baseUrl}' is not an https URL on {provider}'s API domains.", nameof(baseUrl));
        }

        _logger.LogInformation("Saving personal {Provider} API URL to secure storage", provider);
        return _secureStorage.SetAsync(UrlStorageKey(provider), baseUrl.Trim().TrimEnd('/'), ct);
    }

    /// <inheritdoc />
    public async Task<ModApiKeyCheckResult> ValidateKeyAsync(ModApiProvider provider, string? apiKey = null, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(apiKey)
            ? await GetEffectiveKeyAsync(provider, ct).ConfigureAwait(false)
            : apiKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            return new ModApiKeyCheckResult(ModApiKeyCheckStatus.MissingKey);
        }

        try
        {
            var baseUrl = await GetApiBaseUrlAsync(provider, ct).ConfigureAwait(false);
            using var request = BuildValidationRequest(provider, key, baseUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                // Thunderstore answers current-user with 200 even for unknown tokens; only a named user means the token worked.
                if (provider == ModApiProvider.Thunderstore)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!HasThunderstoreUser(body))
                    {
                        return new ModApiKeyCheckResult(ModApiKeyCheckStatus.Invalid, status);
                    }
                }

                return new ModApiKeyCheckResult(ModApiKeyCheckStatus.Valid, status);
            }

            // 429 means the key works but its quota is exhausted: the cue to switch to a personal key.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new ModApiKeyCheckResult(ModApiKeyCheckStatus.RateLimited, status);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ModApiKeyCheckResult(ModApiKeyCheckStatus.Invalid, status);
            }

            return new ModApiKeyCheckResult(ModApiKeyCheckStatus.NetworkError, status, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The URL may carry the key (mod.io), so only the message is logged.
            _logger.LogWarning("Could not reach {Provider} API to validate key: {Message}", provider, ex.Message);
            return new ModApiKeyCheckResult(ModApiKeyCheckStatus.NetworkError, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Builds the cheapest authenticated request each API offers.
    /// </summary>
    internal static HttpRequestMessage BuildValidationRequest(ModApiProvider provider, string key, string? baseUrl = null)
    {
        var root = (baseUrl ?? DefaultApiBaseUrl(provider)).TrimEnd('/');
        HttpRequestMessage request;

        switch (provider)
        {
            case ModApiProvider.CurseForge:
                request = new HttpRequestMessage(HttpMethod.Get, $"{root}/games?pageSize=1");
                request.Headers.Add("x-api-key", key);
                break;
            case ModApiProvider.NexusMods:
                request = new HttpRequestMessage(HttpMethod.Get, $"{root}/users/validate.json");
                request.Headers.Add("apikey", key);
                request.Headers.Add("Application-Name", "BlueStar");
                break;
            case ModApiProvider.ModIo:
                // mod.io read-only keys travel as a query parameter, not a header.
                request = new HttpRequestMessage(HttpMethod.Get, $"{root}/games?_limit=1&api_key={Uri.EscapeDataString(key)}");
                break;
            case ModApiProvider.Thunderstore:
                request = new HttpRequestMessage(HttpMethod.Get, $"{root}/experimental/current-user/");
                request.Headers.Add("Authorization", $"Bearer {key}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }

        request.Headers.Add("Accept", "application/json");
        return request;
    }

    private static bool HasThunderstoreUser(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("username", out var username)
                   && username.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(username.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
