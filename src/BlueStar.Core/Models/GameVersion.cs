using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a logical build/version snapshot of a game across all associated depots,
/// decoupled from any specific host provider.
/// </summary>
public record GameVersion
{
    /// <summary>
    /// Gets the build identifier (e.g. "15961492" or "release-2024").
    /// </summary>
    public string BuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the branch name (e.g. "public", "beta", "steam_legacy").
    /// </summary>
    public string BranchName { get; init; } = "public";

    /// <summary>
    /// Gets the friendly display name for the build in the UI.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the release or update timestamp for this build.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Gets optional description or changelog notes.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets a value indicating whether this build identity is non-canonical or inferred from an external source.
    /// </summary>
    public bool IsInferred { get; init; }

    /// <summary>
    /// Gets a value indicating whether this build is curated and recommended for stable play.
    /// </summary>
    public bool IsCurated { get; init; }

    /// <summary>
    /// Gets the primary source of the build metadata ("SteamCMD", "ManifestHub", "DepotBox", "LocalArchive").
    /// </summary>
    public string Source { get; init; } = "Steam";

    /// <summary>
    /// Gets the depots and specific manifest revisions required for this build.
    /// </summary>
    public IReadOnlyList<DepotVersion> Depots { get; init; } = [];

    /// <summary>
    /// Gets a dictionary mapping DepotId -> ManifestId for quick lookup.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong> DepotManifests =>
        Depots.ToDictionary(d => d.DepotId, d => d.ManifestId);

    /// <summary>
    /// Gets the total size in bytes of all depots in this build.
    /// </summary>
    public long TotalSizeBytes => Depots.Sum(d => d.SizeBytes);

    /// <summary>
    /// Formatted size string for display.
    /// </summary>
    public string FormattedSize => TotalSizeBytes > 0
        ? $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : string.Empty;

    /// <summary>
    /// Formatted date string for display.
    /// </summary>
    public string FormattedDate => UpdatedAt.HasValue
        ? UpdatedAt.Value.ToString("d MMM yyyy")
        : "Unknown Date";

    /// <summary>
    /// Converts this <see cref="GameVersion"/> to a compatible legacy <see cref="GameBuildInfo"/>.
    /// </summary>
    public GameBuildInfo ToGameBuildInfo(bool isCurrentBuild = false) => new()
    {
        BuildId = BuildId,
        BranchName = BranchName,
        DisplayName = DisplayName,
        UpdatedAt = UpdatedAt,
        Description = Description,
        IsCurrentBuild = isCurrentBuild,
        Source = Source,
        DepotManifests = DepotManifests,
        TotalSizeBytes = TotalSizeBytes
    };

    /// <summary>
    /// Creates a <see cref="GameVersion"/> from a legacy <see cref="GameBuildInfo"/>.
    /// </summary>
    public static GameVersion FromGameBuildInfo(GameBuildInfo build, IEnumerable<DepotInfo>? depotDetails = null)
    {
        var depotList = new List<DepotVersion>();
        var detailsLookup = depotDetails?.ToDictionary(d => d.DepotId) ?? [];

        foreach (var (depotId, manifestId) in build.DepotManifests)
        {
            detailsLookup.TryGetValue(depotId, out var existing);
            depotList.Add(new DepotVersion
            {
                DepotId = depotId,
                ManifestId = manifestId,
                Name = existing?.Name ?? $"Depot {depotId}",
                SizeBytes = existing?.SizeBytes ?? 0,
                DepotKey = existing?.DepotKey,
                Category = existing?.Category ?? "Base Game",
                Platform = existing?.Platform ?? "Windows",
                Architecture = existing?.Architecture,
                IsRecommended = existing?.IsRecommended ?? true
            });
        }

        return new GameVersion
        {
            BuildId = build.BuildId,
            BranchName = build.BranchName,
            DisplayName = build.DisplayName,
            UpdatedAt = build.UpdatedAt,
            Description = build.Description,
            Source = build.Source,
            Depots = depotList.AsReadOnly()
        };
    }
}
