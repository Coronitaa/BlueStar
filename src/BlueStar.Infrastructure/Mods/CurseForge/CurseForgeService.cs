using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods.CurseForge;

/// <summary>
/// Client for searching and downloading mods from CurseForge API.
/// </summary>
public sealed class CurseForgeService : ICurseForgeService
{
    private readonly HttpClient _http;
    private readonly IModApiKeyService _apiKeyService;
    private readonly ILogger<CurseForgeService> _logger;
    private readonly string _cacheRoot;

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(1);
    private IReadOnlyList<(int Id, string Name, string Slug)>? _cachedGames;
    private DateTimeOffset _gamesExpiry = DateTimeOffset.MinValue;

    private static readonly Dictionary<uint, int> KnownAppIdToGameId = new()
    {
        [1623730] = 85196, // Palworld (540 mods)
        [413150]  = 669,   // Stardew Valley (1,964 mods)
        [1149460] = 69271, // Minecraft Dungeons (58 mods)
        [1912410] = 69271, // Minecraft Dungeons (58 mods)
        [220200]  = 4401,  // Kerbal Space Program (1,971 mods)
        [262060]  = 608,   // Darkest Dungeon (10 mods)
        [464920]  = 61489, // Surviving Mars (6 mods)
        [945360]  = 69761, // Among Us (52 mods)
        [1222670] = 78062, // The Sims 4 (10,000+ mods)
        [2399830] = 83374, // ARK Survival Ascended (7,253 mods)
        [990080]  = 87986, // Hogwarts Legacy (1,016 mods)
        [1484280] = 78135, // Demeo (13 mods)
        [306130]  = 455,   // The Elder Scrolls Online (112 mods)
        [1376070] = 84062, // Rushdown Revolt (14 mods)
        [2456740] = 88849  // inZOI (6,354 mods)
    };

    private static readonly HashSet<int> InactiveOrDummyGameIds =
    [
        431,   // Terraria (1 dummy mod on CurseForge)
        84137, // Tennis Elbow 4 (1 mod)
        727,   // Civilization VI (2 mods)
        4773,  // Final Fantasy VI (1 mod)
        5230, 5001, 5026, 4741, 5021, // Final Fantasy I-V (0 mods)
        85440, // Helldivers 2 (0 mods)
        65     // StarCraft II (0 mods)
    ];

    public CurseForgeService(
        HttpClient http,
        IModApiKeyService apiKeyService,
        ILogger<CurseForgeService> logger,
        string? cacheRoot = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "curseforge_cache");

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueStar-ModManager", "1.0"));
        }
    }

    /// <inheritdoc />
    public async Task<int?> ResolveGameIdAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.AppId > 0 && KnownAppIdToGameId.TryGetValue(instance.AppId, out var directId))
        {
            return directId;
        }

        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        var games = await GetGamesListAsync(key, ct).ConfigureAwait(false);
        var norm = Normalize(instance.Name);

        var match = games.FirstOrDefault(g => !InactiveOrDummyGameIds.Contains(g.Id) && Normalize(g.Name).Equals(norm, StringComparison.OrdinalIgnoreCase));
        if (match.Id > 0) return match.Id;

        // Strip common copy suffixes like " (2)", " II", etc.
        var cleanName = Regex.Replace(instance.Name, @"\s*\(\d+\)$", "").Trim();
        var cleanNorm = Normalize(cleanName);
        if (cleanNorm != norm)
        {
            var cleanMatch = games.FirstOrDefault(g => !InactiveOrDummyGameIds.Contains(g.Id) && Normalize(g.Name).Equals(cleanNorm, StringComparison.OrdinalIgnoreCase));
            if (cleanMatch.Id > 0) return cleanMatch.Id;
        }

        return null;
    }

    private async Task<IReadOnlyList<(int Id, string Name, string Slug)>> GetGamesListAsync(string key, CancellationToken ct)
    {
        if (_cachedGames != null && DateTimeOffset.UtcNow < _gamesExpiry)
        {
            return _cachedGames;
        }

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/games?pageSize=50");
            req.Headers.Add("x-api-key", key);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<(int, string, string)>();
            foreach (var elem in data.EnumerateArray())
            {
                var id = elem.GetProperty("id").GetInt32();
                var name = elem.GetProperty("name").GetString() ?? string.Empty;
                var slug = elem.TryGetProperty("slug", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                list.Add((id, name, slug));
            }

            _cachedGames = list;
            _gamesExpiry = DateTimeOffset.UtcNow.Add(CacheExpiry);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch games list from CurseForge");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CurseForgeCategory>> GetCategoriesAsync(int gameId, CancellationToken ct = default)
    {
        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return [];

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/categories?gameId={gameId}");
            req.Headers.Add("x-api-key", key);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var categories = new List<CurseForgeCategory>();
            foreach (var elem in data.EnumerateArray())
            {
                var id = elem.GetProperty("id").GetInt32();
                var gId = elem.TryGetProperty("gameId", out var g) ? g.GetInt32() : gameId;
                var name = elem.GetProperty("name").GetString() ?? string.Empty;
                var slug = elem.TryGetProperty("slug", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var iconUrl = elem.TryGetProperty("iconUrl", out var ico) ? ico.GetString() : null;

                categories.Add(new CurseForgeCategory
                {
                    Id = id,
                    GameId = gId,
                    Name = name,
                    Slug = slug,
                    IconUrl = iconUrl
                });
            }

            return categories.OrderBy(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load categories for CurseForge game {GameId}", gameId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CurseForgeMod>> SearchModsAsync(
        int gameId,
        string? query = null,
        int? categoryId = null,
        string sortBy = "Popularity",
        bool isAscending = false,
        int index = 0,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await SearchModsWithCountAsync(gameId, query, categoryId, sortBy, isAscending, index, pageSize, ct).ConfigureAwait(false);
        return result.Mods;
    }

    /// <inheritdoc />
    public async Task<CurseForgeSearchResult> SearchModsWithCountAsync(
        int gameId,
        string? query = null,
        int? categoryId = null,
        string sortBy = "Popularity",
        bool isAscending = false,
        int index = 0,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return new CurseForgeSearchResult([], 0);

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
            var sortField = sortBy switch
            {
                "Featured" or "Destacados" => 1,
                "Popularity" or "Popularidad" => 2,
                "Updated" or "Actualizado" => 3,
                "Name" or "Nombre" => 4,
                "Downloads" or "Descargas" or "TotalDownloads" => 6,
                _ => 2
            };

            var sortOrder = isAscending ? "asc" : "desc";
            var url = $"{baseUrl.TrimEnd('/')}/mods/search?gameId={gameId}&sortField={sortField}&sortOrder={sortOrder}&index={index}&pageSize={pageSize}";

            if (!string.IsNullOrWhiteSpace(query))
            {
                url += $"&searchFilter={Uri.EscapeDataString(query.Trim())}";
            }
            if (categoryId.HasValue && categoryId.Value > 0)
            {
                url += $"&categoryId={categoryId.Value}";
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", key);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new CurseForgeSearchResult([], 0);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new CurseForgeSearchResult([], 0);
            }

            var totalCount = 0;
            if (doc.RootElement.TryGetProperty("pagination", out var pag) && pag.TryGetProperty("totalCount", out var tc))
            {
                totalCount = tc.GetInt32();
            }

            var mods = new List<CurseForgeMod>();
            foreach (var elem in data.EnumerateArray())
            {
                var id = elem.GetProperty("id").GetInt32();
                var name = elem.GetProperty("name").GetString() ?? string.Empty;
                var slug = elem.TryGetProperty("slug", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var summary = elem.TryGetProperty("summary", out var sum) ? sum.GetString() ?? string.Empty : string.Empty;
                var downloadCount = elem.TryGetProperty("downloadCount", out var dl) ? dl.GetInt64() : 0;
                var dateMod = elem.TryGetProperty("dateModified", out var dm) && dm.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.MinValue;

                string? webUrl = null;
                if (elem.TryGetProperty("links", out var links) && links.TryGetProperty("websiteUrl", out var wu))
                {
                    webUrl = wu.GetString();
                }

                string? thumbUrl = null;
                if (elem.TryGetProperty("logo", out var logo))
                {
                    thumbUrl = logo.TryGetProperty("thumbnailUrl", out var tu) ? tu.GetString() : logo.TryGetProperty("url", out var u) ? u.GetString() : null;
                }

                string? author = null;
                if (elem.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                {
                    author = authors.EnumerateArray().FirstOrDefault().TryGetProperty("name", out var an) ? an.GetString() : null;
                }

                var catList = new List<string>();
                if (elem.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cats.EnumerateArray())
                    {
                        var cn = c.TryGetProperty("name", out var cname) ? cname.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(cn)) catList.Add(cn);
                    }
                }

                var filesList = new List<CurseForgeFile>();
                if (elem.TryGetProperty("latestFiles", out var lf) && lf.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in lf.EnumerateArray())
                    {
                        var fid = f.GetProperty("id").GetInt32();
                        var fDisplayName = f.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;
                        var fFileName = f.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;
                        var fLen = f.TryGetProperty("fileLength", out var fl) ? fl.GetInt64() : 0;
                        var fDlCount = f.TryGetProperty("downloadCount", out var fdc) ? fdc.GetInt64() : 0;
                        var fDate = f.TryGetProperty("fileDate", out var fd) && fd.TryGetDateTimeOffset(out var fdo) ? fdo : DateTimeOffset.MinValue;
                        var fUrl = f.TryGetProperty("downloadUrl", out var du) ? du.GetString() : null;

                        filesList.Add(new CurseForgeFile
                        {
                            Id = fid,
                            GameId = gameId,
                            ModId = id,
                            DisplayName = string.IsNullOrWhiteSpace(fDisplayName) ? fFileName : fDisplayName,
                            FileName = fFileName,
                            FileLength = fLen,
                            DownloadCount = fDlCount,
                            FileDate = fDate,
                            DownloadUrl = fUrl
                        });
                    }
                }

                mods.Add(new CurseForgeMod
                {
                    Id = id,
                    GameId = gameId,
                    Name = name,
                    Slug = slug,
                    Summary = summary,
                    WebsiteUrl = webUrl,
                    ThumbnailUrl = thumbUrl,
                    PrimaryAuthor = author,
                    DownloadCount = downloadCount,
                    DateModified = dateMod,
                    Categories = catList,
                    Files = filesList
                });
            }

            return new CurseForgeSearchResult(mods, totalCount > 0 ? totalCount : mods.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search CurseForge mods for game {GameId}", gameId);
            return new CurseForgeSearchResult([], 0);
        }
    }

    /// <inheritdoc />
    public async Task<ModPackage?> DownloadModAsync(
        int gameId,
        CurseForgeMod mod,
        CurseForgeFile? file = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mod);

        var targetFile = file ?? mod.LatestFile;
        if (targetFile == null)
        {
            _logger.LogWarning("No files available for CurseForge mod {ModName}", mod.Name);
            return null;
        }

        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        var downloadUrl = targetFile.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            // Fallback: Query download-url endpoint
            downloadUrl = await ResolveDownloadUrlAsync(mod.Id, targetFile.Id, key, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            _logger.LogWarning("Could not resolve download URL for CurseForge mod {ModName} file {FileId}", mod.Name, targetFile.Id);
            return null;
        }

        var dir = Path.Combine(_cacheRoot, gameId.ToString());
        Directory.CreateDirectory(dir);

        var safeFileName = string.IsNullOrWhiteSpace(targetFile.FileName)
            ? $"{mod.Id}_{targetFile.Id}.zip"
            : $"{mod.Id}_{targetFile.Id}_{Path.GetFileName(targetFile.FileName)}";
        var destPath = Path.Combine(dir, safeFileName);

        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
        {
            progress?.Report(100.0);
            return CreatePackage(destPath, mod, gameId, targetFile);
        }

        try
        {
            progress?.Report(5.0);
            using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            dlReq.Headers.Add("x-api-key", key);

            using var response = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? targetFile.FileLength;
            var tempFile = Path.Combine(Path.GetTempPath(), $"cf_{Guid.NewGuid():N}.tmp");

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = Math.Min(95.0, 5.0 + ((double)totalRead / totalBytes * 90.0));
                        progress?.Report(pct);
                    }
                }
            }

            File.Move(tempFile, destPath, overwrite: true);
            progress?.Report(100.0);

            return CreatePackage(destPath, mod, gameId, targetFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download CurseForge mod {Mod} file {FileId}", mod.Name, targetFile.Id);
            return null;
        }
    }

    private async Task<string?> ResolveDownloadUrlAsync(int modId, int fileId, string key, CancellationToken ct)
    {
        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.CurseForge, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/mods/{modId}/files/{fileId}/download-url");
            req.Headers.Add("x-api-key", key);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static ModPackage CreatePackage(string filePath, CurseForgeMod mod, int gameId, CurseForgeFile file)
    {
        return new ModPackage
        {
            Path = filePath,
            Name = mod.Name,
            Source = new ModSourceRef
            {
                Provider = "curseforge",
                RemoteId = $"{gameId}:{mod.Id}:{file.Id}",
                Version = file.DisplayName,
                PageUrl = mod.WebsiteUrl ?? $"https://www.curseforge.com/",
                IconUrl = mod.ThumbnailUrl
            },
            Layout = ModPackageLayout.Unknown
        };
    }

    private static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var clean = Regex.Replace(name, @"[^\w\s-]", "").Trim().ToLowerInvariant();
        return Regex.Replace(clean, @"\s+", " ");
    }
}
