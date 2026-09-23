using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Catalog;

/// <summary>
/// SQLite FTS5-backed local catalog repository providing offline search, deterministic sorting,
/// and faceted querying across the entire application catalog.
/// </summary>
public sealed class LocalCatalogRepository : ILocalCatalogRepository
{
    private readonly string _dbPath;
    private readonly ILogger<LocalCatalogRepository> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public LocalCatalogRepository(ILogger<LocalCatalogRepository> logger, string? dbPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            _dbPath = dbPath;
        }
        else
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "catalog");
            Directory.CreateDirectory(appData);
            _dbPath = Path.Combine(appData, "catalog.sqlite");
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = csb.ToString();
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS apps (
                    app_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    normalized_name TEXT NOT NULL,
                    compact_name TEXT NOT NULL,
                    app_type TEXT NOT NULL,
                    last_modified INTEGER NOT NULL,
                    price_change_number INTEGER NOT NULL DEFAULT 0,
                    review_percent INTEGER,
                    review_count INTEGER,
                    positive_reviews INTEGER,
                    negative_reviews INTEGER,
                    rating_updated_at INTEGER,
                    header_image_url TEXT,
                    price_text TEXT,
                    discount_percent INTEGER NOT NULL DEFAULT 0,
                    release_date_text TEXT,
                    has_windows INTEGER NOT NULL DEFAULT 1,
                    has_mac INTEGER NOT NULL DEFAULT 0,
                    has_linux INTEGER NOT NULL DEFAULT 0,
                    is_nsfw INTEGER NOT NULL DEFAULT 0,
                    has_drm INTEGER NOT NULL DEFAULT 0,
                    has_external_launcher INTEGER NOT NULL DEFAULT 0,
                    tag_ids TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_apps_normalized ON apps(normalized_name);
                CREATE INDEX IF NOT EXISTS idx_apps_compact ON apps(compact_name);
                CREATE INDEX IF NOT EXISTS idx_apps_review_percent ON apps(review_percent);
                CREATE INDEX IF NOT EXISTS idx_apps_last_modified ON apps(last_modified);

                CREATE VIRTUAL TABLE IF NOT EXISTS apps_fts USING fts5(
                    name,
                    normalized_name,
                    compact_name,
                    content='apps',
                    content_rowid='app_id',
                    tokenize = 'unicode61 remove_diacritics 2'
                );

                CREATE TRIGGER IF NOT EXISTS apps_ai AFTER INSERT ON apps BEGIN
                    INSERT INTO apps_fts(rowid, name, normalized_name, compact_name)
                    VALUES (new.app_id, new.name, new.normalized_name, new.compact_name);
                END;

                CREATE TRIGGER IF NOT EXISTS apps_ad AFTER DELETE ON apps BEGIN
                    INSERT INTO apps_fts(apps_fts, rowid, name, normalized_name, compact_name)
                    VALUES ('delete', old.app_id, old.name, old.normalized_name, old.compact_name);
                END;

                CREATE TRIGGER IF NOT EXISTS apps_au AFTER UPDATE ON apps BEGIN
                    INSERT INTO apps_fts(apps_fts, rowid, name, normalized_name, compact_name)
                    VALUES ('delete', old.app_id, old.name, old.normalized_name, old.compact_name);
                    INSERT INTO apps_fts(rowid, name, normalized_name, compact_name)
                    VALUES (new.app_id, new.name, new.normalized_name, new.compact_name);
                END;
                """;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
            _logger.LogInformation("LocalCatalogRepository initialized at {Path}", _dbPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertAppsAsync(IEnumerable<CatalogAppItem> apps, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = """
            INSERT INTO apps (
                app_id, name, normalized_name, compact_name, app_type, last_modified,
                price_change_number, review_percent, review_count, positive_reviews,
                negative_reviews, rating_updated_at, header_image_url, price_text,
                discount_percent, release_date_text, has_windows, has_mac, has_linux,
                is_nsfw, has_drm, has_external_launcher, tag_ids
            ) VALUES (
                @app_id, @name, @normalized_name, @compact_name, @app_type, @last_modified,
                @price_change_number, @review_percent, @review_count, @positive_reviews,
                @negative_reviews, @rating_updated_at, @header_image_url, @price_text,
                @discount_percent, @release_date_text, @has_windows, @has_mac, @has_linux,
                @is_nsfw, @has_drm, @has_external_launcher, @tag_ids
            )
            ON CONFLICT(app_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                compact_name = excluded.compact_name,
                app_type = excluded.app_type,
                last_modified = excluded.last_modified,
                price_change_number = excluded.price_change_number,
                review_percent = COALESCE(excluded.review_percent, apps.review_percent),
                review_count = COALESCE(excluded.review_count, apps.review_count),
                positive_reviews = COALESCE(excluded.positive_reviews, apps.positive_reviews),
                negative_reviews = COALESCE(excluded.negative_reviews, apps.negative_reviews),
                rating_updated_at = COALESCE(excluded.rating_updated_at, apps.rating_updated_at),
                header_image_url = COALESCE(excluded.header_image_url, apps.header_image_url),
                price_text = COALESCE(excluded.price_text, apps.price_text),
                discount_percent = excluded.discount_percent,
                release_date_text = COALESCE(excluded.release_date_text, apps.release_date_text),
                has_windows = excluded.has_windows,
                has_mac = excluded.has_mac,
                has_linux = excluded.has_linux,
                is_nsfw = excluded.is_nsfw,
                has_drm = excluded.has_drm,
                has_external_launcher = excluded.has_external_launcher,
                tag_ids = COALESCE(excluded.tag_ids, apps.tag_ids);
            """;

        var pAppId = cmd.Parameters.Add("@app_id", SqliteType.Integer);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pNorm = cmd.Parameters.Add("@normalized_name", SqliteType.Text);
        var pCompact = cmd.Parameters.Add("@compact_name", SqliteType.Text);
        var pType = cmd.Parameters.Add("@app_type", SqliteType.Text);
        var pLastMod = cmd.Parameters.Add("@last_modified", SqliteType.Integer);
        var pPriceChg = cmd.Parameters.Add("@price_change_number", SqliteType.Integer);
        var pRevPct = cmd.Parameters.Add("@review_percent", SqliteType.Integer);
        var pRevCnt = cmd.Parameters.Add("@review_count", SqliteType.Integer);
        var pPosRev = cmd.Parameters.Add("@positive_reviews", SqliteType.Integer);
        var pNegRev = cmd.Parameters.Add("@negative_reviews", SqliteType.Integer);
        var pRatingDate = cmd.Parameters.Add("@rating_updated_at", SqliteType.Integer);
        var pImg = cmd.Parameters.Add("@header_image_url", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("@price_text", SqliteType.Text);
        var pDiscount = cmd.Parameters.Add("@discount_percent", SqliteType.Integer);
        var pRelease = cmd.Parameters.Add("@release_date_text", SqliteType.Text);
        var pWin = cmd.Parameters.Add("@has_windows", SqliteType.Integer);
        var pMac = cmd.Parameters.Add("@has_mac", SqliteType.Integer);
        var pLin = cmd.Parameters.Add("@has_linux", SqliteType.Integer);
        var pNsfw = cmd.Parameters.Add("@is_nsfw", SqliteType.Integer);
        var pDrm = cmd.Parameters.Add("@has_drm", SqliteType.Integer);
        var pLauncher = cmd.Parameters.Add("@has_external_launcher", SqliteType.Integer);
        var pTags = cmd.Parameters.Add("@tag_ids", SqliteType.Text);

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();

            pAppId.Value = app.AppId;
            pName.Value = app.Name;
            pNorm.Value = string.IsNullOrWhiteSpace(app.NormalizedName)
                ? DeterministicNormalizer.Normalize(app.Name)
                : app.NormalizedName;
            pCompact.Value = string.IsNullOrWhiteSpace(app.CompactName)
                ? DeterministicNormalizer.ToCompactKey(app.Name)
                : app.CompactName;
            pType.Value = app.AppType;
            pLastMod.Value = app.LastModified;
            pPriceChg.Value = app.PriceChangeNumber;
            pRevPct.Value = (object?)app.ReviewPercent ?? DBNull.Value;
            pRevCnt.Value = (object?)app.ReviewCount ?? DBNull.Value;
            pPosRev.Value = (object?)app.PositiveReviews ?? DBNull.Value;
            pNegRev.Value = (object?)app.NegativeReviews ?? DBNull.Value;
            pRatingDate.Value = app.RatingUpdatedAt.HasValue
                ? app.RatingUpdatedAt.Value.ToUnixTimeSeconds()
                : DBNull.Value;
            pImg.Value = (object?)app.HeaderImageUrl ?? DBNull.Value;
            pPrice.Value = (object?)app.PriceText ?? DBNull.Value;
            pDiscount.Value = app.DiscountPercent;
            pRelease.Value = (object?)app.ReleaseDateText ?? DBNull.Value;
            pWin.Value = app.HasWindows ? 1 : 0;
            pMac.Value = app.HasMac ? 1 : 0;
            pLin.Value = app.HasLinux ? 1 : 0;
            pNsfw.Value = app.IsNsfw ? 1 : 0;
            pDrm.Value = app.HasDrm ? 1 : 0;
            pLauncher.Value = app.HasExternalLauncher ? 1 : 0;
            pTags.Value = app.TagIds.Count > 0
                ? "," + string.Join(',', app.TagIds) + ","
                : DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CatalogAppItem?> GetByAppIdAsync(uint appId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM apps WHERE app_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", appId);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadApp(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogAppItem>> SearchCandidatesAsync(string term, int limit = 100, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        // 1. Direct AppID lookup
        if (SteamQueryParser.TryParseAppId(term, out var appId))
        {
            var single = await GetByAppIdAsync(appId, ct).ConfigureAwait(false);
            if (single != null) return [single];
        }

        var normalized = DeterministicNormalizer.Normalize(term);
        var compact = DeterministicNormalizer.ToCompactKey(term);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var results = new List<CatalogAppItem>();
        var seenIds = new HashSet<uint>();

        // 2. Exact match check (Name, NormalizedName, or CompactName)
        using (var exactCmd = connection.CreateCommand())
        {
            exactCmd.CommandText = """
                SELECT * FROM apps 
                WHERE normalized_name = @norm OR compact_name = @compact OR name = @raw
                LIMIT @limit
                """;
            exactCmd.Parameters.AddWithValue("@norm", normalized);
            exactCmd.Parameters.AddWithValue("@compact", compact);
            exactCmd.Parameters.AddWithValue("@raw", term.Trim());
            exactCmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await exactCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var app = ReadApp(reader);
                if (seenIds.Add(app.AppId)) results.Add(app);
            }
        }

        if (results.Count >= limit) return results;

        // 3. FTS5 Query
        var ftsTokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*")
            .ToList();

        if (ftsTokens.Count > 0)
        {
            var ftsQuery = string.Join(" AND ", ftsTokens);
            try
            {
                using var ftsCmd = connection.CreateCommand();
                ftsCmd.CommandText = """
                    SELECT a.* FROM apps_fts f
                    JOIN apps a ON f.rowid = a.app_id
                    WHERE apps_fts MATCH @query
                    ORDER BY rank
                    LIMIT @limit
                    """;
                ftsCmd.Parameters.AddWithValue("@query", ftsQuery);
                ftsCmd.Parameters.AddWithValue("@limit", limit - results.Count);

                using var reader = await ftsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var app = ReadApp(reader);
                    if (seenIds.Add(app.AppId)) results.Add(app);
                }
            }
            catch (SqliteException ex)
            {
                _logger.LogDebug(ex, "FTS5 syntax error for query '{Query}', falling back to LIKE", ftsQuery);
            }
        }

        if (results.Count >= limit) return results;

        // 4. Prefix and Substring Fallback
        using (var likeCmd = connection.CreateCommand())
        {
            likeCmd.CommandText = """
                SELECT * FROM apps 
                WHERE normalized_name LIKE @prefix OR compact_name LIKE @compactPrefix
                LIMIT @limit
                """;
            likeCmd.Parameters.AddWithValue("@prefix", normalized + "%");
            likeCmd.Parameters.AddWithValue("@compactPrefix", compact + "%");
            likeCmd.Parameters.AddWithValue("@limit", limit - results.Count);

            using var reader = await likeCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var app = ReadApp(reader);
                if (seenIds.Add(app.AppId)) results.Add(app);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<CatalogAppItem> Items, int TotalCount)> QueryAsync(
        LocalCatalogQuery query, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        // Text search integration
        bool hasFts = false;
        string? ftsQuery = null;

        if (!string.IsNullOrWhiteSpace(query.Term))
        {
            if (SteamQueryParser.TryParseAppId(query.Term, out var directAppId))
            {
                whereClauses.Add("a.app_id = @directAppId");
                parameters.Add(new SqliteParameter("@directAppId", directAppId));
            }
            else
            {
                var normalized = DeterministicNormalizer.Normalize(query.Term);
                var tokens = normalized
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*")
                    .ToList();

                if (tokens.Count > 0)
                {
                    ftsQuery = string.Join(" AND ", tokens);
                    hasFts = true;
                    parameters.Add(new SqliteParameter("@ftsQuery", ftsQuery));
                }
            }
        }

        // Platform filters
        if (query.HasWindows == true) whereClauses.Add("a.has_windows = 1");
        if (query.HasMac == true) whereClauses.Add("a.has_mac = 1");
        if (query.HasLinux == true) whereClauses.Add("a.has_linux = 1");

        // AppType filters (998 = Games, 994 = Software)
        if (!string.IsNullOrWhiteSpace(query.AppTypes))
        {
            var types = query.AppTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasGames = types.Contains("998") || types.Contains("game", StringComparer.OrdinalIgnoreCase);
            var hasSoftware = types.Contains("994") || types.Contains("software", StringComparer.OrdinalIgnoreCase);

            if (hasGames && !hasSoftware)
            {
                whereClauses.Add("LOWER(a.app_type) = 'game'");
            }
            else if (hasSoftware && !hasGames)
            {
                whereClauses.Add("LOWER(a.app_type) IN ('software', 'application', 'tool', 'utility')");
            }
        }

        // Content filters
        if (query.NoDrm == true) whereClauses.Add("a.has_drm = 0");
        if (query.NoExternalLauncher == true) whereClauses.Add("a.has_external_launcher = 0");
        if (query.HideAdult == true) whereClauses.Add("a.is_nsfw = 0");
        if (query.DiscountedOnly == true) whereClauses.Add("a.discount_percent > 0");

        // Rating filters
        if (query.MinRatingPercent.HasValue)
        {
            whereClauses.Add("a.review_percent >= @minRating");
            parameters.Add(new SqliteParameter("@minRating", query.MinRatingPercent.Value));
        }
        if (query.MaxRatingPercent.HasValue)
        {
            whereClauses.Add("a.review_percent < @maxRating");
            parameters.Add(new SqliteParameter("@maxRating", query.MaxRatingPercent.Value));
        }

        // Tags included
        if (query.IncludedTagIds != null && query.IncludedTagIds.Count > 0)
        {
            var idx = 0;
            foreach (var tagId in query.IncludedTagIds)
            {
                var pName = $"@incTag{idx++}";
                whereClauses.Add($"a.tag_ids LIKE {pName}");
                parameters.Add(new SqliteParameter(pName, $"%,{tagId},%"));
            }
        }

        // Tags excluded
        if (query.ExcludedTagIds != null && query.ExcludedTagIds.Count > 0)
        {
            var idx = 0;
            foreach (var tagId in query.ExcludedTagIds)
            {
                var pName = $"@excTag{idx++}";
                whereClauses.Add($"(a.tag_ids IS NULL OR a.tag_ids NOT LIKE {pName})");
                parameters.Add(new SqliteParameter(pName, $"%,{tagId},%"));
            }
        }

        // Restrict to AppIds
        if (query.RestrictToAppIds != null && query.RestrictToAppIds.Count > 0)
        {
            var idList = string.Join(',', query.RestrictToAppIds);
            whereClauses.Add($"a.app_id IN ({idList})");
        }

        var fromClause = hasFts
            ? "FROM apps_fts f JOIN apps a ON f.rowid = a.app_id"
            : "FROM apps a";

        if (hasFts)
        {
            whereClauses.Insert(0, "apps_fts MATCH @ftsQuery");
        }

        var whereSql = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : string.Empty;

        // 1. Total count
        int totalCount = 0;
        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) {fromClause} {whereSql};";
            foreach (var p in parameters) countCmd.Parameters.Add(p);

            var countRes = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (countRes is long l) totalCount = (int)l;
            else if (countRes is int i) totalCount = i;
        }

        // 2. Sorting
        var dir = query.Descending ? "DESC" : "ASC";
        var sortSql = (query.SortBy ?? string.Empty).ToLowerInvariant() switch
        {
            "name" => $"ORDER BY a.name {dir}",
            "released" => $"ORDER BY a.last_modified {dir}",
            "reviews" => $"ORDER BY a.review_percent {dir} NULLS LAST, a.review_count DESC",
            "reviewcount" or "reviews_count" => $"ORDER BY a.review_count {dir} NULLS LAST",
            "price" => $"ORDER BY (CASE WHEN a.price_text IS NULL OR a.price_text = '' THEN 999999 WHEN LOWER(a.price_text) LIKE '%free%' OR LOWER(a.price_text) LIKE '%gratis%' THEN 0 ELSE CAST(REPLACE(REPLACE(REPLACE(a.price_text, '$', ''), '€', ''), ',', '.') AS REAL) END) {dir}, a.last_modified DESC",
            "discount" => $"ORDER BY a.discount_percent {dir}",
            "appid" => $"ORDER BY a.app_id {dir}",
            _ => hasFts ? "ORDER BY rank" : $"ORDER BY a.last_modified {dir}"
        };

        // 3. Paging
        var limitSql = $"LIMIT {query.Limit} OFFSET {query.Offset}";

        var selectSql = $"""
            SELECT a.* {fromClause}
            {whereSql}
            {sortSql}
            {limitSql};
            """;

        var items = new List<CatalogAppItem>();
        using (var dataCmd = connection.CreateCommand())
        {
            dataCmd.CommandText = selectSql;
            foreach (var p in parameters)
            {
                dataCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }

            using var reader = await dataCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(ReadApp(reader));
            }
        }

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM apps;";
        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return res is long l ? (int)l : Convert.ToInt32(res, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task ImportSnapshotAsync(string sqliteFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteFilePath);
        if (!File.Exists(sqliteFilePath))
            throw new FileNotFoundException("Snapshot database file not found.", sqliteFilePath);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection.ClearAllPools();
            var backupDir = Path.GetDirectoryName(_dbPath)!;
            Directory.CreateDirectory(backupDir);

            File.Copy(sqliteFilePath, _dbPath, overwrite: true);
            _initialized = false;
        }
        finally
        {
            _lock.Release();
        }

        await InitializeAsync(ct).ConfigureAwait(false);
    }

    private static CatalogAppItem ReadApp(SqliteDataReader reader)
    {
        var appId = (uint)reader.GetInt64(reader.GetOrdinal("app_id"));
        var name = reader.GetString(reader.GetOrdinal("name"));
        var normalizedName = reader.GetString(reader.GetOrdinal("normalized_name"));
        var compactName = reader.GetString(reader.GetOrdinal("compact_name"));
        var appType = reader.GetString(reader.GetOrdinal("app_type"));
        var lastModified = reader.GetInt64(reader.GetOrdinal("last_modified"));
        var priceChangeNumber = (uint)reader.GetInt64(reader.GetOrdinal("price_change_number"));

        int? reviewPercent = reader.IsDBNull(reader.GetOrdinal("review_percent"))
            ? null : reader.GetInt32(reader.GetOrdinal("review_percent"));
        int? reviewCount = reader.IsDBNull(reader.GetOrdinal("review_count"))
            ? null : reader.GetInt32(reader.GetOrdinal("review_count"));
        int? posReviews = reader.IsDBNull(reader.GetOrdinal("positive_reviews"))
            ? null : reader.GetInt32(reader.GetOrdinal("positive_reviews"));
        int? negReviews = reader.IsDBNull(reader.GetOrdinal("negative_reviews"))
            ? null : reader.GetInt32(reader.GetOrdinal("negative_reviews"));

        DateTimeOffset? ratingUpdatedAt = reader.IsDBNull(reader.GetOrdinal("rating_updated_at"))
            ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("rating_updated_at")));

        var headerImageUrl = reader.IsDBNull(reader.GetOrdinal("header_image_url"))
            ? null : reader.GetString(reader.GetOrdinal("header_image_url"));
        var priceText = reader.IsDBNull(reader.GetOrdinal("price_text"))
            ? null : reader.GetString(reader.GetOrdinal("price_text"));
        var discountPercent = reader.GetInt32(reader.GetOrdinal("discount_percent"));
        var releaseDateText = reader.IsDBNull(reader.GetOrdinal("release_date_text"))
            ? null : reader.GetString(reader.GetOrdinal("release_date_text"));

        var hasWindows = reader.GetInt32(reader.GetOrdinal("has_windows")) == 1;
        var hasMac = reader.GetInt32(reader.GetOrdinal("has_mac")) == 1;
        var hasLinux = reader.GetInt32(reader.GetOrdinal("has_linux")) == 1;
        var isNsfw = reader.GetInt32(reader.GetOrdinal("is_nsfw")) == 1;
        var hasDrm = reader.GetInt32(reader.GetOrdinal("has_drm")) == 1;
        var hasExternalLauncher = reader.GetInt32(reader.GetOrdinal("has_external_launcher")) == 1;

        var tagIdsStr = reader.IsDBNull(reader.GetOrdinal("tag_ids"))
            ? null : reader.GetString(reader.GetOrdinal("tag_ids"));
        var tagIds = new List<int>();
        if (!string.IsNullOrWhiteSpace(tagIdsStr))
        {
            var parts = tagIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (int.TryParse(p, out var tid)) tagIds.Add(tid);
            }
        }

        return new CatalogAppItem
        {
            AppId = appId,
            Name = name,
            NormalizedName = normalizedName,
            CompactName = compactName,
            AppType = appType,
            LastModified = lastModified,
            PriceChangeNumber = priceChangeNumber,
            ReviewPercent = reviewPercent,
            ReviewCount = reviewCount,
            PositiveReviews = posReviews,
            NegativeReviews = negReviews,
            RatingUpdatedAt = ratingUpdatedAt,
            HeaderImageUrl = headerImageUrl,
            PriceText = priceText,
            DiscountPercent = discountPercent,
            ReleaseDateText = releaseDateText,
            HasWindows = hasWindows,
            HasMac = hasMac,
            HasLinux = hasLinux,
            IsNsfw = isNsfw,
            HasDrm = hasDrm,
            HasExternalLauncher = hasExternalLauncher,
            TagIds = tagIds
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }
}
