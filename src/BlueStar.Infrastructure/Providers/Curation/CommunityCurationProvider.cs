using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Curation;

/// <summary>
/// Implements <see cref="IRecommendationProvider"/> using a local persistent curation store
/// and optional remote curated advice feeds.
/// </summary>
public sealed class CommunityCurationProvider : IRecommendationProvider
{
    private readonly string _curationFilePath;
    private readonly ILogger<CommunityCurationProvider> _logger;
    private readonly ConcurrentDictionary<uint, CurationRecommendation> _curationCache = new();
    private bool _isLoaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string ProviderId => "community_curation";
    public string DisplayName => "Community & Curated Knowledge";
    public int Priority => 100;

    public ProviderCapabilities Capabilities => ProviderCapabilities.CurationAdvice;

    public CommunityCurationProvider(
        ILogger<CommunityCurationProvider> logger,
        string? curationFilePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _curationFilePath = curationFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "cache", "curation.json");
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_isLoaded) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isLoaded) return;

            if (File.Exists(_curationFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_curationFilePath, ct).ConfigureAwait(false);
                    var items = JsonSerializer.Deserialize<Dictionary<string, CurationRecommendation>>(json);
                    if (items != null)
                    {
                        foreach (var kvp in items)
                        {
                            if (uint.TryParse(kvp.Key, out var appId))
                            {
                                _curationCache[appId] = kvp.Value;
                            }
                        }
                    }
                    _logger.LogInformation("Loaded {Count} curated game recommendations from {Path}", _curationCache.Count, _curationFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load curated recommendations from {Path}", _curationFilePath);
                }
            }

            _isLoaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CurationRecommendation?> GetRecommendationAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _curationCache.TryGetValue(appId, out var rec) ? rec : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, CurationRecommendation>> BatchGetRecommendationsAsync(IEnumerable<uint> appIds, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<uint, CurationRecommendation>();
        foreach (var appId in appIds)
        {
            if (_curationCache.TryGetValue(appId, out var rec))
            {
                result[appId] = rec;
            }
        }
        return result;
    }

    /// <summary>
    /// Saves or updates a curated recommendation for testing or user override.
    /// </summary>
    public async Task SaveRecommendationAsync(CurationRecommendation recommendation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        _curationCache[recommendation.AppId] = recommendation;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_curationFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var dict = new Dictionary<string, CurationRecommendation>();
            foreach (var kvp in _curationCache)
            {
                dict[kvp.Key.ToString()] = kvp.Value;
            }

            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_curationFilePath, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
