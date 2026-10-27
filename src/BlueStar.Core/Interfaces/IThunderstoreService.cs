using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service for discovering, searching, and downloading mods from Thunderstore.
/// </summary>
public interface IThunderstoreService
{
    /// <summary>
    /// Checks if the given game instance is supported on Thunderstore, returning its community identifier (e.g. "lethal-company") or null.
    /// </summary>
    Task<string?> ResolveCommunityAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Gets all communities hosted on Thunderstore.
    /// </summary>
    Task<IReadOnlyList<ThunderstoreCommunity>> GetCommunitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all mod packages available for the specified community.
    /// </summary>
    Task<IReadOnlyList<ThunderstorePackage>> GetPackagesAsync(string community, CancellationToken ct = default);

    /// <summary>
    /// Searches and filters packages for the specified community by text query, category, and sorting.
    /// Supports multiple included and excluded category tags.
    /// </summary>
    Task<IReadOnlyList<ThunderstorePackage>> SearchPackagesAsync(
        string community,
        string? query = null,
        string? category = null,
        string sortBy = "Downloads",
        bool isAscending = false,
        CancellationToken ct = default,
        IEnumerable<string>? includedCategories = null,
        IEnumerable<string>? excludedCategories = null);

    /// <summary>
    /// Downloads a Thunderstore package into the local cache and returns a <see cref="ModPackage"/> ready for installation.
    /// </summary>
    Task<ModPackage?> DownloadPackageAsync(
        string community,
        ThunderstorePackage package,
        ThunderstorePackageVersion? version = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
