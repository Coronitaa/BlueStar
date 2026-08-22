using System.Text.Json;
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

    public AppSettingsService(ILogger<AppSettingsService> logger, string? settingsPath = null)
    {
        _logger = logger;
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "settings.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        LoadSettings();
    }

    /// <summary>Last directory the user selected as game install path.</summary>
    public string? LastInstallDirectory => _current.LastInstallDirectory;

    /// <summary>Whether to delete downloaded depot files and archives after installation completes.</summary>
    public bool DeleteDepotsAfterInstall => _current.DeleteDepotsAfterInstall;

    /// <summary>Default backend API base URL.</summary>
    public string DefaultApiUrl => string.IsNullOrWhiteSpace(_current.DefaultApiUrl) ? "https://depotbox.org" : _current.DefaultApiUrl;

    /// <summary>Default backend API key configured in backend.</summary>
    public string? DefaultApiKey => _current.DefaultApiKey;

    /// <summary>Persists a new last install directory.</summary>
    public async Task SetLastInstallDirectoryAsync(string path)
    {
        _current = _current with { LastInstallDirectory = path };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the delete depots setting.</summary>
    public async Task SetDeleteDepotsAfterInstallAsync(bool delete)
    {
        _current = _current with { DeleteDepotsAfterInstall = delete };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default API base URL.</summary>
    public async Task SetDefaultApiUrlAsync(string url)
    {
        _current = _current with { DefaultApiUrl = string.IsNullOrWhiteSpace(url) ? "https://depotbox.org" : url.Trim().TrimEnd('/') };
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the default backend API key.</summary>
    public async Task SetDefaultApiKeyAsync(string? key)
    {
        _current = _current with { DefaultApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim() };
        await SaveAsync().ConfigureAwait(false);
    }

    private void LoadSettings()
    {
        _lock.Wait();
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
            _logger.LogDebug("Loaded app settings from {Path}", _settingsPath);
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

    private sealed record AppSettings
    {
        public string? LastInstallDirectory { get; init; }
        public bool DeleteDepotsAfterInstall { get; init; } = true;
        public string? DefaultApiUrl { get; init; } = "https://depotbox.org";
        public string? DefaultApiKey { get; init; }
    }
}
