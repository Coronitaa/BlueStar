using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Query criteria applied locally to the offline SQLite + FTS5 catalog.
/// Filters and sorts operate across the entire candidate universe rather than an arbitrary 50-item page.
/// </summary>
public sealed class LocalCatalogQuery
{
    public string? Term { get; init; }
    public IReadOnlyCollection<int>? IncludedTagIds { get; init; }
    public IReadOnlyCollection<int>? ExcludedTagIds { get; init; }
    public string? AppTypes { get; init; } = SteamStoreFacets.AppTypeAll;
    public bool? HasWindows { get; init; }
    public bool? HasMac { get; init; }
    public bool? HasLinux { get; init; }
    public int? MinRatingPercent { get; init; }
    public int? MaxRatingPercent { get; init; }
    public bool? NoDrm { get; init; }
    public bool? NoExternalLauncher { get; init; }
    public bool? HideAdult { get; init; }
    public bool? DiscountedOnly { get; init; }
    public int? MaxPriceCents { get; init; }
    public string? SortBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Offset { get; init; } = 0;
    public int Limit { get; init; } = 50;
    public IReadOnlyCollection<uint>? RestrictToAppIds { get; init; }
}

/// <summary>
/// Interface for the local SQLite FTS5 catalog repository.
/// Serves as the primary source of truth for game identity, offline search, and instant querying.
/// </summary>
public interface ILocalCatalogRepository : IDisposable
{
    /// <summary>
    /// Initializes SQLite tables, indexes, and virtual FTS5 tables.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts one or more catalog items into the local SQLite database.
    /// </summary>
    Task UpsertAppsAsync(IEnumerable<CatalogAppItem> apps, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single catalog item by its numeric AppID.
    /// </summary>
    Task<CatalogAppItem?> GetByAppIdAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Searches local candidates matching a search term using AppID, prefix, FTS5, and compact aliases.
    /// </summary>
    Task<IReadOnlyList<CatalogAppItem>> SearchCandidatesAsync(string term, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Queries the catalog with faceted filters, full-text search, deterministic sorting, and pagination.
    /// </summary>
    Task<(IReadOnlyList<CatalogAppItem> Items, int TotalCount)> QueryAsync(LocalCatalogQuery query, CancellationToken ct = default);

    /// <summary>
    /// Gets the total number of indexed applications in the local catalog.
    /// </summary>
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Imports or replaces the catalog database from an external snapshot SQLite file.
    /// </summary>
    Task ImportSnapshotAsync(string sqliteFilePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the current completeness and metadata coverage of the local catalog index.
    /// </summary>
    Task<CatalogCompleteness> GetCompletenessAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the count of applications associated with specific tag IDs from the local catalog.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetTagCountsAsync(IEnumerable<int> tagIds, CancellationToken ct = default);

    /// <summary>
    /// Sets a persistent catalog metadata key-value pair (e.g. snapshot_version, expected_app_count).
    /// </summary>
    Task SetMetadataAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a persistent catalog metadata value by key.
    /// </summary>
    Task<string?> GetMetadataAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all indexed App IDs in the catalog.
    /// </summary>
    Task<IReadOnlyList<uint>> GetAllAppIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates enriched metadata (tags, release dates, prices, ratings, platforms) for a batch of apps in a single transaction.
    /// </summary>
    Task UpdateAppMetadataBatchAsync(IEnumerable<AppMetadataEnrichment> batch, CancellationToken ct = default);

    /// <summary>
    /// Updates DRM, external launcher, and DLC count for a specific app.
    /// </summary>
    Task UpdateAppDrmAndLauncherAsync(uint appId, string? drmName, string? launcherName, int? dlcCount, CancellationToken ct = default);
}

