using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Cache;

/// <summary>
/// File-system based cache service storing serialized JSON entries with expiration support.
/// </summary>
public sealed class FileCacheService : ICacheService
{
    private readonly string _cachePath;
    private readonly ILogger<FileCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FileCacheService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public FileCacheService(ILogger<FileCacheService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "cache");
        Directory.CreateDirectory(_cachePath);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return default;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<CacheEntry<T>>(json, JsonOptions);

            if (entry is null)
                return default;

            // Check expiration
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Cache entry expired for key: {Key}", key);
                File.Delete(filePath);
                return default;
            }

            return entry.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read cache entry for key: {Key}", key);
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var entry = new CacheEntry<T>
        {
            Value = value,
            ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow.Add(expiry.Value) : null
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var filePath = GetFilePath(key);

        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        _logger.LogDebug("Cached entry for key: {Key}, expires: {Expiry}", key, entry.ExpiresAt);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var filePath = GetFilePath(key);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(_cachePath))
        {
            foreach (var file in Directory.GetFiles(_cachePath, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(file);
            }
        }

        _logger.LogInformation("Cache cleared.");
        return Task.CompletedTask;
    }

    private string GetFilePath(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(_cachePath, $"{hash}.json");
    }

    private sealed record CacheEntry<T>
    {
        public T? Value { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }
}
