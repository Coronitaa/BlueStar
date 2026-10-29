using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service for searching and downloading mods from CurseForge.
/// </summary>
public interface ICurseForgeService
{
    /// <summary>
    /// Checks if the given game instance is supported on CurseForge, returning its game ID or null.
    /// </summary>
    Task<int?> ResolveGameIdAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Gets categories for the given game on CurseForge.
    /// </summary>
    Task<IReadOnlyList<CurseForgeCategory>> GetCategoriesAsync(int gameId, CancellationToken ct = default);

    /// <summary>
    /// Searches mods on CurseForge for the given game.
    /// </summary>
    Task<IReadOnlyList<CurseForgeMod>> SearchModsAsync(
        int gameId,
        string? query = null,
        int? categoryId = null,
        string sortBy = "Popularity",
        bool isAscending = false,
        int index = 0,
        int pageSize = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Searches mods on CurseForge for the given game, returning matching mods and total count.
    /// </summary>
    Task<CurseForgeSearchResult> SearchModsWithCountAsync(
        int gameId,
        string? query = null,
        int? categoryId = null,
        string sortBy = "Popularity",
        bool isAscending = false,
        int index = 0,
        int pageSize = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads a CurseForge mod file into local cache and returns a <see cref="ModPackage"/> ready for installation.
    /// </summary>
    Task<ModPackage?> DownloadModAsync(
        int gameId,
        CurseForgeMod mod,
        CurseForgeFile? file = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
