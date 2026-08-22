using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Information about a Steam Workshop item.
/// </summary>
public record WorkshopItemInfo(
    ulong PublishedFileId,
    uint AppId,
    string Title,
    string Description,
    string? PreviewUrl,
    ulong FileSizeBytes,
    string? Author,
    DateTimeOffset? UpdatedAt,
    string? FileUrl = null
);

/// <summary>
/// Service for checking Steam Workshop availability, querying item metadata, and downloading Workshop mods.
/// </summary>
public interface IWorkshopService
{
    /// <summary>
    /// Checks if a Steam game has Steam Workshop enabled.
    /// </summary>
    Task<bool> HasWorkshopSupportAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Parses a Steam Workshop URL or raw ID into a numeric PublishedFileId.
    /// </summary>
    ulong? ParsePublishedFileId(string input);

    /// <summary>
    /// Fetches details and metadata for a Steam Workshop item.
    /// </summary>
    Task<WorkshopItemInfo?> GetItemDetailsAsync(ulong publishedFileId, CancellationToken ct = default);

    /// <summary>
    /// Downloads and installs a Steam Workshop item into the specified target mod directory.
    /// </summary>
    Task<bool> DownloadAndInstallItemAsync(
        uint appId,
        ulong publishedFileId,
        string targetModDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads, adapts and installs a Steam Workshop item automatically into the game instance's resolved mod location.
    /// </summary>
    Task<bool> DownloadAndInstallItemAsync(
        BlueStar.Core.Models.GameInstance instance,
        ulong publishedFileId,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the optimal primary mod directory for a game instance.
    /// </summary>
    string ResolveModDirectory(BlueStar.Core.Models.GameInstance instance);
}
