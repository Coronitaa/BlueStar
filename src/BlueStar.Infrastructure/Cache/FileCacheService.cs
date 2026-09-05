using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Cache;

/// <summary>
/// Multi-tier cache service storing in-memory L1 entries with persistent L2 file-system backing and expiration support.
/// </summary>
public sealed class FileCacheService : ICacheService
{
    private readonly string _cachePath;
    private readonly ILogger<FileCacheService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry<object>> _memoryCache = new(StringComparer.Ordinal);
    private const int MaxL1Entries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FileCacheService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public FileCacheService(ILogger<FileCacheService> logger, string? cachePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachePath = !string.IsNullOrWhiteSpace(cachePath)
            ? cachePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "cache");
        try
        {
            Directory.CreateDirectory(_cachePath);
        }
        catch { }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = DateTimeOffset.UtcNow;

        // 1. Check L1 Memory Cache
        if (_memoryCache.TryGetValue(key, out var memEntry))
        {
            if (memEntry.ExpiresAt.HasValue && memEntry.ExpiresAt.Value < now)
            {
                _memoryCache.TryRemove(key, out _);
                var expiredPath = GetFilePath(key);
                try { if (File.Exists(expiredPath)) File.Delete(expiredPath); } catch { }
                return default;
            }

            if (memEntry.Value is T typedValue)
            {
                return typedValue;
            }
            if (memEntry.Value is JsonElement jsonElement)
            {
                try
                {
                    var converted = jsonElement.Deserialize<T>(JsonOptions);
                    if (converted is not null)
                    {
                        _memoryCache[key] = new CacheEntry<object> { Value = converted, ExpiresAt = memEntry.ExpiresAt };
                        return converted;
                    }
                }
                catch { }
            }
        }

        // 2. Check L2 Disk Cache
        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return default;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<CacheEntry<T>>(json, JsonOptions);

            if (entry is null)
            {
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                return default;
            }

            // Check expiration
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < now)
            {
                _logger.LogDebug("Cache entry expired for key: {Key}", key);
                _memoryCache.TryRemove(key, out _);
                try { File.Delete(filePath); } catch { }
                return default;
            }

            // Populate L1 cache
            if (entry.Value is not null)
            {
                if (_memoryCache.Count >= MaxL1Entries)
                {
                    PruneL1Cache();
                }
                _memoryCache[key] = new CacheEntry<object> { Value = entry.Value, ExpiresAt = entry.ExpiresAt };
            }

            return entry.Value;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt cache file detected for key: {Key}. Safely discarding corrupt cache.", key);
            _memoryCache.TryRemove(key, out _);
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            return default;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error reading cache entry for key: {Key}", key);
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var expiresAt = expiry.HasValue ? DateTimeOffset.UtcNow.Add(expiry.Value) : (DateTimeOffset?)null;

        // 1. Update L1 Memory Cache
        if (value is not null)
        {
            if (_memoryCache.Count >= MaxL1Entries)
            {
                PruneL1Cache();
            }
            _memoryCache[key] = new CacheEntry<object> { Value = value, ExpiresAt = expiresAt };
        }

        // 2. Persist to L2 Disk Cache atomically
        var entry = new CacheEntry<T>
        {
            Value = value,
            ExpiresAt = expiresAt
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var filePath = GetFilePath(key);
        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(tempFilePath, json, ct).ConfigureAwait(false);
            File.Move(tempFilePath, filePath, overwrite: true);
            _logger.LogDebug("Cached entry for key: {Key}, expires: {Expiry}", key, entry.ExpiresAt);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            _logger.LogWarning(ex, "Failed writing cache file for key: {Key}", key);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        _memoryCache.TryRemove(key, out _);

        var filePath = GetFilePath(key);
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch { }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _memoryCache.Clear();

        if (Directory.Exists(_cachePath))
        {
            foreach (var file in Directory.GetFiles(_cachePath, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
        }

        _logger.LogInformation("Cache cleared.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs non-blocking cleanup of expired disk cache files.
    /// </summary>
    public async Task SweepExpiredEntriesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_cachePath)) return;

        var now = DateTimeOffset.UtcNow;
        var files = Directory.GetFiles(_cachePath, "*.json");

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("expiresAt", out var expEl) &&
                    expEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(expEl.GetString(), out var expiresAt) &&
                    expiresAt < now)
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    private void PruneL1Cache()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _memoryCache
            .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
            .Select(kvp => kvp.Key)
            .Take(100)
            .ToList();

        foreach (var k in expiredKeys)
        {
            _memoryCache.TryRemove(k, out _);
        }

        // If still over limit, drop oldest entries
        if (_memoryCache.Count >= MaxL1Entries)
        {
            var excessKeys = _memoryCache.Keys.Take(200).ToList();
            foreach (var k in excessKeys)
            {
                _memoryCache.TryRemove(k, out _);
            }
        }
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
