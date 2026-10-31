using System.Text.Json;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Persists user-facing application preferences to a JSON file.
/// Currently stores: LastInstallDirectory.
/// </summary>
public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private readonly ILogger<AppSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private AppSettings _current = new();
    private readonly Lazy<BundledSecrets?> _bundledSecrets;

    public AppSettingsService(ILogger<AppSettingsService> logger, string? settingsPath = null, BundledSecrets? bundledSecrets = null)
    {
        _logger = logger;
        // An explicit settings path means tests or tools: only use secrets they pass in, never the build's own
        _bundledSecrets = bundledSecrets != null
            ? new Lazy<BundledSecrets?>(bundledSecrets)
            : new Lazy<BundledSecrets?>(() => settingsPath == null ? BundledSecrets.Load() : null);

        if (settingsPath != null)
        {
            _settingsPath = settingsPath;
        }
        else
        {
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "settings.json");
            var portableMarker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".portable");

            if (File.Exists(portableMarker))
            {
                _settingsPath = localPath;
            }
            else if (File.Exists(appDataPath))
            {
                _settingsPath = appDataPath;
            }
            else if (File.Exists(localPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appDataPath)!);
                    File.Copy(localPath, appDataPath, overwrite: false);
                    _settingsPath = appDataPath;
                }
                catch
                {
                    _settingsPath = localPath;
                }
            }
            else
            {
                _settingsPath = appDataPath;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        LoadSettings();
    }

    /// <summary>Fired whenever application settings are updated and saved.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Last directory the user selected as game install path.</summary>
    public string? LastInstallDirectory => _current.LastInstallDirectory;

    /// <summary>Default directory where downloaded games and instances are installed.</summary>
    public string DefaultDownloadDirectory =>
        !string.IsNullOrWhiteSpace(_current.DefaultDownloadDirectory)
            ? _current.DefaultDownloadDirectory
            : (!string.IsNullOrWhiteSpace(_current.LastInstallDirectory)
                ? _current.LastInstallDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"));

    /// <summary>Whether to delete downloaded depot files and archives after installation completes.</summary>
    public bool DeleteDepotsAfterInstall => _current.DeleteDepotsAfterInstall;

    /// <summary>Default backend API base URL.</summary>
    public string DefaultApiUrl
    {
        get
        {
            var envUrl = Environment.GetEnvironmentVariable("BLUESTAR_DEPOTBOX_API_URL")
                         ?? Environment.GetEnvironmentVariable("DEPOTBOX_API_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
            {
                return envUrl.Trim().TrimEnd('/');
            }

            return string.IsNullOrWhiteSpace(_current.DefaultApiUrl) ? "https://depotbox.org" : _current.DefaultApiUrl;
        }
    }

    /// <summary>Default backend API key configured in backend.</summary>
    public string? DefaultApiKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_current.DefaultApiKey) && _current.DefaultApiKey != "YOUR-API-KEY")
            {
                return _current.DefaultApiKey;
            }

            var envKey = Environment.GetEnvironmentVariable("BLUESTAR_DEPOTBOX_API_KEY")
                         ?? Environment.GetEnvironmentVariable("DEPOTBOX_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                return envKey.Trim();
            }

            // Built-in key: decrypted in memory only, never copied into the user's settings
            var bundledKey = _bundledSecrets.Value?.DefaultApiKey;
            return IsRealApiKey(bundledKey) ? bundledKey!.Trim() : null;
        }
    }

    /// <summary>
    /// The key the user entered in Settings, or null when BlueStar uses its built-in one. The Settings page shows
    /// this instead of <see cref="DefaultApiKey"/> so the built-in key is never displayed.
    /// </summary>
    public string? UserDefaultApiKey => IsRealApiKey(_current.DefaultApiKey) ? _current.DefaultApiKey!.Trim() : null;

    /// <summary>Whether to show NSFW / adult content in catalogs and searches (default false).</summary>
    public bool ShowNsfwContent => _current.ShowNsfwContent;

    /// <summary>Whether to show games with 3rd-party DRM in catalogs and searches (default true).</summary>
    public bool ShowDrmContent => _current.ShowDrmContent;

    /// <summary>Whether Explore shows result cards in a grid rather than a row list.</summary>
    public bool ExploreGridView => _current.ExploreGridView;

    /// <summary>Persists a new last install directory.</summary>
    public async Task SetLastInstallDirectoryAsync(string path)
    {
        _current = _current with { LastInstallDirectory = path };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default download and installation directory.</summary>
    public async Task SetDefaultDownloadDirectoryAsync(string path)
    {
        _current = _current with { DefaultDownloadDirectory = path, LastInstallDirectory = path };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the delete depots setting.</summary>
    public async Task SetDeleteDepotsAfterInstallAsync(bool delete)
    {
        _current = _current with { DeleteDepotsAfterInstall = delete };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the show NSFW content setting.</summary>
    public async Task SetShowNsfwContentAsync(bool show)
    {
        _current = _current with { ShowNsfwContent = show };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the show DRM content setting.</summary>
    public async Task SetShowDrmContentAsync(bool show)
    {
        _current = _current with { ShowDrmContent = show };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Remembers whether Explore was last left on the card grid or the row list.</summary>
    public async Task SetExploreGridViewAsync(bool grid)
    {
        if (_current.ExploreGridView == grid) return;

        _current = _current with { ExploreGridView = grid };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Default sort criterion for Explore catalog (default "Reviews").</summary>
    public string DefaultExploreSort => string.IsNullOrWhiteSpace(_current.DefaultExploreSort) ? "Reviews" : _current.DefaultExploreSort;

    /// <summary>Default sort direction (descending or ascending) for Explore catalog (default true).</summary>
    public bool DefaultExploreSortDescending => _current.DefaultExploreSortDescending;

    /// <summary>Default store section / curated list for Explore catalog (default "popularnew").</summary>
    public string DefaultExploreStoreList => _current.DefaultExploreStoreList ?? "popularnew";

    /// <summary>Persists the default Explore sort criterion.</summary>
    public async Task SetDefaultExploreSortAsync(string sort)
    {
        _current = _current with { DefaultExploreSort = sort };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default Explore sort direction.</summary>
    public async Task SetDefaultExploreSortDescendingAsync(bool descending)
    {
        _current = _current with { DefaultExploreSortDescending = descending };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default Explore store section / list.</summary>
    public async Task SetDefaultExploreStoreListAsync(string storeList)
    {
        _current = _current with { DefaultExploreStoreList = storeList };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Whether experimental mod management and the Mods tab are enabled (default false).</summary>
    public bool EnableExperimentalMods => _current.EnableExperimentalMods;

    /// <summary>Persists the experimental mod management setting.</summary>
    public async Task SetEnableExperimentalModsAsync(bool enable)
    {
        _current = _current with { EnableExperimentalMods = enable };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default API base URL.</summary>

    public async Task SetDefaultApiUrlAsync(string url)
    {
        _current = _current with { DefaultApiUrl = string.IsNullOrWhiteSpace(url) ? "https://depotbox.org" : url.Trim().TrimEnd('/') };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Whether to automatically analyze and verify system requirements upon application startup (default true).</summary>
    public bool CheckSystemRequirementsOnStartup => _current.CheckSystemRequirementsOnStartup;

    /// <summary>Persists the check system requirements on startup setting.</summary>
    public async Task SetCheckSystemRequirementsOnStartupAsync(bool check)
    {
        _current = _current with { CheckSystemRequirementsOnStartup = check };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default backend API key.</summary>
    public async Task SetDefaultApiKeyAsync(string? key)
    {
        _current = _current with { DefaultApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim() };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Environment variable that supplies the built-in key for a mod provider.</summary>
    public static string ModApiKeyEnvironmentVariable(ModApiProvider provider) => provider switch
    {
        ModApiProvider.CurseForge => "BLUESTAR_CURSEFORGE_API_KEY",
        ModApiProvider.NexusMods => "BLUESTAR_NEXUSMODS_API_KEY",
        ModApiProvider.ModIo => "BLUESTAR_MODIO_API_KEY",
        ModApiProvider.Thunderstore => "BLUESTAR_THUNDERSTORE_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    /// <summary>Environment variable that supplies the built-in mod.io API path.</summary>
    public const string ModIoApiUrlEnvironmentVariable = "BLUESTAR_MODIO_API_URL";

    /// <summary>
    /// Built-in API base URL for a mod provider, or null when it has none configured.
    /// Only mod.io uses one today (its per-account API path). Same lookup order as the keys.
    /// </summary>
    public string? GetBuiltInModApiBaseUrl(ModApiProvider provider)
    {
        if (provider != ModApiProvider.ModIo) return null;

        var envUrl = Environment.GetEnvironmentVariable(ModIoApiUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envUrl)) return envUrl.Trim();

        var bundledUrl = _bundledSecrets.Value?.ModIoApiUrl;
        if (!string.IsNullOrWhiteSpace(bundledUrl)) return bundledUrl.Trim();

        var userUrl = _current.ModApiKeys?.ModIoApiUrl;
        return string.IsNullOrWhiteSpace(userUrl) ? null : userUrl.Trim();
    }

    /// <summary>
    /// Built-in key for a mod provider. Looks at the environment variable first, then the encrypted keys shipped
    /// with the release (<see cref="BundledSecrets"/>), then the user's own settings.json.
    /// </summary>
    public string? GetBuiltInModApiKey(ModApiProvider provider)
    {
        var envKey = Environment.GetEnvironmentVariable(ModApiKeyEnvironmentVariable(provider));
        if (IsRealApiKey(envKey)) return envKey!.Trim();

        var bundledKey = BundledModApiKey(provider);
        if (IsRealApiKey(bundledKey)) return bundledKey!.Trim();

        var userKey = _current.ModApiKeys?.Get(provider);
        return IsRealApiKey(userKey) ? userKey!.Trim() : null;
    }

    private static bool IsRealApiKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Trim() != "YOUR-API-KEY";

    private string? BundledModApiKey(ModApiProvider provider)
    {
        var secrets = _bundledSecrets.Value;
        return provider switch
        {
            ModApiProvider.CurseForge => secrets?.CurseForge,
            ModApiProvider.NexusMods => secrets?.NexusMods,
            ModApiProvider.ModIo => secrets?.ModIo,
            ModApiProvider.Thunderstore => secrets?.Thunderstore,
            _ => null
        };
    }

    private void LoadSettings()
    {
        _lock.Wait();
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
                _logger.LogDebug("Loaded app settings from {Path}", _settingsPath);
            }

            // Releases up to 1.4.8 copied the built-in key into the user's settings.json in plain text. Remove it:
            // BlueStar now reads it from the encrypted bundle, so it no longer has to live there.
            var bundledKey = _bundledSecrets.Value?.DefaultApiKey;
            if (IsRealApiKey(bundledKey) && string.Equals(_current.DefaultApiKey?.Trim(), bundledKey!.Trim(), StringComparison.Ordinal))
            {
                _current = _current with { DefaultApiKey = null };
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_current, JsonOptions));
                _logger.LogInformation("Removed the built-in DepotBox API key from the user's settings file");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load app settings");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save app settings");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Application language code ("en" or "es").</summary>
    public string Language => string.IsNullOrWhiteSpace(_current.Language) ? "en" : _current.Language;

    /// <summary>Language exactly as stored: null or "system" until BlueStar picks one on first launch.</summary>
    public string? ConfiguredLanguage => _current.Language;

    /// <summary>Persists the language setting.</summary>
    public async Task SetLanguageAsync(string language)
    {
        _current = _current with { Language = language };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Whether advanced build/manifest options are enabled.</summary>
    public bool EnableAdvancedBuildOptions => _current.EnableAdvancedBuildOptions;

    /// <summary>Sets and persists whether advanced build/manifest options are enabled.</summary>
    public async Task SetEnableAdvancedBuildOptionsAsync(bool enabled)
    {
        _current = _current with { EnableAdvancedBuildOptions = enabled };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Whether the advanced debug system and live diagnostics are enabled (default false).</summary>
    public bool EnableDebugSystem => _current.EnableDebugSystem;

    /// <summary>Sets and persists whether the debug system is enabled.</summary>
    public async Task SetEnableDebugSystemAsync(bool enabled)
    {
        _current = _current with { EnableDebugSystem = enabled };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Remote manifest URL for catalog snapshot updates.</summary>
    public string? CatalogManifestUrl => _current.CatalogManifestUrl ?? "https://github.com/Coronitaa/BlueStar-Catalog/releases/latest/download/catalog-manifest.json";

    /// <summary>Sets and persists the catalog manifest URL.</summary>
    public async Task SetCatalogManifestUrlAsync(string? url)
    {
        _current = _current with { CatalogManifestUrl = url };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Whether to hide the Force DLC Warning modal.</summary>
    public bool HideForceDlcWarning => _current.HideForceDlcWarning;

    /// <summary>Persists the Hide Force DLC Warning setting.</summary>
    public async Task SetHideForceDlcWarningAsync(bool hide)
    {
        _current = _current with { HideForceDlcWarning = hide };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Number of columns for the instances grid (0 means List View).</summary>
    public int InstancesColumnCount => _current.InstancesColumnCount;

    /// <summary>Persists the instances column count setting.</summary>
    public async Task SetInstancesColumnCountAsync(int count)
    {
        _current = _current with { InstancesColumnCount = count };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Overridden Steam Content Server Cell ID for DepotDownloader (0 = Automatic / fastest for user).
    /// </summary>
    public int DownloadServerCellId => _current.DownloadServerCellId;

    /// <summary>Persists the Steam download server Cell ID setting.</summary>
    public async Task SetDownloadServerCellIdAsync(int cellId)
    {
        _current = _current with { DownloadServerCellId = cellId };
        await SaveAsync().ConfigureAwait(false);
    }

    private sealed record AppSettings
    {
        public string? Language { get; init; }
        public string? LastInstallDirectory { get; init; }
        public string? DefaultDownloadDirectory { get; init; }
        public bool DeleteDepotsAfterInstall { get; init; } = true;
        public bool ShowNsfwContent { get; init; } = false;
        public bool ShowDrmContent { get; init; } = true;
        public bool ExploreGridView { get; init; } = true;
        public bool EnableExperimentalMods { get; init; } = false;
        public bool CheckSystemRequirementsOnStartup { get; init; } = true;
        public bool EnableAdvancedBuildOptions { get; init; } = false;
        public bool EnableDebugSystem { get; init; } = false;
        public string? CatalogManifestUrl { get; init; }

        public string? DefaultExploreSort { get; init; } = "Reviews";
        public bool DefaultExploreSortDescending { get; init; } = true;
    public string? DefaultExploreStoreList { get; init; } = "popularnew";

    public string? DefaultApiUrl { get; init; } = "https://depotbox.org";
    public string? DefaultApiKey { get; init; } = "YOUR-API-KEY";
    public bool HideForceDlcWarning { get; init; } = false;
    public int InstancesColumnCount { get; init; } = 3;
        public int DownloadServerCellId { get; init; } = 0;
    public ModApiKeys? ModApiKeys { get; init; }
}

    /// <summary>Built-in mod provider keys as written to settings.json under "modApiKeys".</summary>
    private sealed record ModApiKeys
    {
        public string? CurseForge { get; init; }
        public string? NexusMods { get; init; }
        public string? ModIo { get; init; }
        public string? Thunderstore { get; init; }
        public string? ModIoApiUrl { get; init; }

        public string? Get(ModApiProvider provider) => provider switch
        {
            ModApiProvider.CurseForge => CurseForge,
            ModApiProvider.NexusMods => NexusMods,
            ModApiProvider.ModIo => ModIo,
            ModApiProvider.Thunderstore => Thunderstore,
            _ => null
        };
    }
}
