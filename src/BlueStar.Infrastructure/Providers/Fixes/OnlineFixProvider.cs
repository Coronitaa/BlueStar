using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Fixes;

/// <summary>
/// Implements <see cref="IFixProvider"/> for the community OnlineFix catalog
/// (e.g. onlinefix.manifesthub.uk API and direct download mirrors).
/// </summary>
public sealed class OnlineFixProvider : IFixProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OnlineFixProvider> _logger;
    private readonly ICacheService? _cache;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;

    public string ProviderId => "onlinefix";
    public string DisplayName => "OnlineFix (Multiplayer)";
    public int Priority => 300;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.FixCatalog |
        ProviderCapabilities.FixDownload;

    public OnlineFixProvider(
        HttpClient http,
        ILogger<OnlineFixProvider> logger,
        ICacheService? cache = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
        _coordinator = coordinator ?? RequestCoordinator.Instance;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<bool> HasFixesAsync(uint appId, CancellationToken ct = default)
    {
        var fixes = await GetFixesAsync(appId, ct).ConfigureAwait(false);
        return fixes.Count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameFixInfo>> GetFixesAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var cacheKey = $"onlinefix_fixes_{appId}";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<GameFixInfo>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("OnlineFix", $"api/games?search={appId}");
                    return cached;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"onlinefix_{appId}", async innerCt =>
        {
            if (_cache != null)
            {
                try
                {
                    var cached = await _cache.GetAsync<List<GameFixInfo>>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        _metrics?.OnCacheHit("OnlineFix", $"api/games?search={appId}");
                        return (IReadOnlyList<GameFixInfo>)cached;
                    }
                }
                catch { }
            }

            try
            {
                _metrics?.OnProviderRequest("OnlineFix", $"https://onlinefix.manifesthub.uk/api/games?search={appId}");
                var url = $"https://onlinefix.manifesthub.uk/api/games?search={appId}";
                using var resp = await _http.GetAsync(url, innerCt).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (_cache != null)
                    {
                        try { await _cache.SetAsync(cacheKey, new List<GameFixInfo>(), TimeSpan.FromDays(1), innerCt).ConfigureAwait(false); } catch { }
                    }
                    return [];
                }

                var json = await resp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

            var list = new List<GameFixInfo>();

            // Expected schema: { "data": [ { "id": "...", "title": "...", "appId": 70, "downloads": [ ... ] } ] }
            // or flat array [ { ... } ]
            JsonElement arrayElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                arrayElement = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("data", out var dProp) && dProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = dProp;
            }
            else if (doc.RootElement.TryGetProperty("games", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = gProp;
            }
            else
            {
                return [];
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var title = item.TryGetProperty("title", out var tProp) ? tProp.GetString() : (item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null);

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

                string? downloadUrl = null;
                if (item.TryGetProperty("downloadUrl", out var dlProp))
                {
                    downloadUrl = dlProp.GetString();
                }
                else if (item.TryGetProperty("downloads", out var dlsProp) && dlsProp.ValueKind == JsonValueKind.Array && dlsProp.GetArrayLength() > 0)
                {
                    var firstDl = dlsProp[0];
                    if (firstDl.TryGetProperty("url", out var uProp))
                    {
                        downloadUrl = uProp.GetString();
                    }
                }

                list.Add(new GameFixInfo
                {
                    Id = $"onlinefix_{id}",
                    Name = $"{title} (OnlineFix)",
                    DownloadName = $"{id}_onlinefix.rar",
                    Tags = ["online", "onlinefix", "multiplayer"],
                    Description = "Multiplayer / Steamworks online emulation fix provided by OnlineFix community.",
                    DownloadUrl = downloadUrl,
                    PositiveVotes = 10,
                    NegativeVotes = 0
                });
            }

            if (_cache != null)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, list, list.Count > 0 ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1), innerCt).ConfigureAwait(false);
                }
                catch { }
            }

            return (IReadOnlyList<GameFixInfo>)list.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query OnlineFix API for AppId {AppId}", appId);
            return [];
        }
    }, ct).ConfigureAwait(false);
}

    /// <inheritdoc />
    public async Task<string> DownloadFixAsync(GameFixInfo fix, string targetDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fix);

        if (string.IsNullOrWhiteSpace(fix.DownloadUrl))
        {
            throw new InvalidOperationException($"No download URL available for fix {fix.Name}.");
        }

        Directory.CreateDirectory(targetDirectory);
        var targetFile = Path.Combine(targetDirectory, fix.DownloadName);

        using var response = await _http.GetAsync(fix.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;

            if (totalBytes > 0 && progress != null)
            {
                var percentage = (double)totalRead / totalBytes * 100.0;
                progress.Report(new DownloadProgress
                {
                    TotalBytes = totalBytes,
                    DownloadedBytes = totalRead,
                    Percentage = percentage,
                    CurrentFile = fix.DownloadName
                });
            }
        }

        return targetFile;
    }
}
