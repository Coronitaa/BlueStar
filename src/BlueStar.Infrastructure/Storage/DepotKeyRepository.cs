using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Implements persistent depot decryption key storage and resolution.
/// Aggregates keys from local cache, community key registries, and provider Lua/JSON payloads.
/// </summary>
public partial class DepotKeyRepository : IDepotKeyRepository
{
    private readonly string _storageFilePath;
    private readonly ILogger<DepotKeyRepository> _logger;
    private readonly ConcurrentDictionary<uint, string> _keys = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private bool _isLoaded;

    [GeneratedRegex(@"addappid\s*\(\s*(\d+)\s*,\s*\d+\s*,\s*[""']([0-9a-fA-F]{64})[""']\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex LuaKeyRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex HexKey64Regex();

    public DepotKeyRepository(
        ILogger<DepotKeyRepository> logger,
        string? storageFilePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storageFilePath = storageFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "cache", "depotkeys.json");

        try
        {
            var dir = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create directory for depot keys repository at {Path}", _storageFilePath);
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_isLoaded) return;

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isLoaded) return;

            if (File.Exists(_storageFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_storageFilePath, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (uint.TryParse(prop.Name, out var depotId))
                            {
                                var keyStr = prop.Value.ValueKind == JsonValueKind.String
                                    ? prop.Value.GetString()
                                    : (prop.Value.TryGetProperty("key", out var kProp) ? kProp.GetString() : null);

                                var clean = NormalizeKey(keyStr);
                                if (clean != null)
                                {
                                    _keys[depotId] = clean;
                                }
                            }
                        }
                    }
                    _logger.LogInformation("Loaded {Count} depot keys from {Path}", _keys.Count, _storageFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load cached depot keys from {Path}", _storageFilePath);
                }
            }

            _isLoaded = true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = _keys.ToDictionary(k => k.Key.ToString(), v => v.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            var temp = _storageFilePath + $".tmp_{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, _storageFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist depot keys to {Path}", _storageFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetKeyAsync(uint depotId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _keys.TryGetValue(depotId, out var key) ? key : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, string>> GetKeysAsync(IEnumerable<uint> depotIds, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<uint, string>();
        foreach (var id in depotIds)
        {
            if (_keys.TryGetValue(id, out var key))
            {
                result[id] = key;
            }
        }
        return result;
    }

    /// <inheritdoc />
    public async Task RegisterKeyAsync(uint depotId, string hexKey, string source = "Manual", CancellationToken ct = default)
    {
        var clean = NormalizeKey(hexKey);
        if (clean == null) return;

        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (_keys.TryGetValue(depotId, out var existing) && string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _keys[depotId] = clean;
        _logger.LogDebug("Registered depot key for Depot {DepotId} (Source: {Source})", depotId, source);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RegisterKeysAsync(IEnumerable<KeyValuePair<uint, string>> keys, string source = "Batch", CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var updated = false;

        foreach (var kvp in keys)
        {
            var clean = NormalizeKey(kvp.Value);
            if (clean != null && (!_keys.TryGetValue(kvp.Key, out var existing) || !string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase)))
            {
                _keys[kvp.Key] = clean;
                updated = true;
            }
        }

        if (updated)
        {
            await SaveAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ImportFromJsonAsync(string jsonContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jsonContent)) return;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var parsed = new List<KeyValuePair<uint, string>>();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out var depotId))
                    {
                        string? keyStr = null;
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            keyStr = prop.Value.GetString();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (prop.Value.TryGetProperty("decryptionkey", out var dkProp)) keyStr = dkProp.GetString();
                            else if (prop.Value.TryGetProperty("key", out var kProp)) keyStr = kProp.GetString();
                        }

                        var clean = NormalizeKey(keyStr);
                        if (clean != null)
                        {
                            parsed.Add(new KeyValuePair<uint, string>(depotId, clean));
                        }
                    }
                }
            }

            if (parsed.Count > 0)
            {
                await RegisterKeysAsync(parsed, "JSON Import", ct).ConfigureAwait(false);
                _logger.LogInformation("Imported {Count} depot keys from JSON", parsed.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse depot keys JSON");
        }
    }

    /// <inheritdoc />
    public async Task ImportFromLuaAsync(string luaContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(luaContent)) return;

        var matches = LuaKeyRegex().Matches(luaContent);
        var parsed = new List<KeyValuePair<uint, string>>();

        foreach (Match m in matches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var depotId))
            {
                var clean = NormalizeKey(m.Groups[2].Value);
                if (clean != null)
                {
                    parsed.Add(new KeyValuePair<uint, string>(depotId, clean));
                }
            }
        }

        if (parsed.Count > 0)
        {
            await RegisterKeysAsync(parsed, "Lua Import", ct).ConfigureAwait(false);
            _logger.LogInformation("Imported {Count} depot keys from Lua script", parsed.Count);
        }
    }

    private static string? NormalizeKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;
        var clean = rawKey.Trim().ToLowerInvariant();
        return HexKey64Regex().IsMatch(clean) ? clean : null;
    }
}
