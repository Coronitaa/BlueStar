using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a game fix or emulator option provided by the DepotBox API (/api/game-fixes).
/// </summary>
public record GameFixInfo
{
    /// <summary>
    /// Unique identifier of the fix in the DepotBox catalog.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Clean display name of the game or fix.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Clean download filename (e.g. "007_First_Light_bypass.zip").
    /// </summary>
    public required string DownloadName { get; init; }

    /// <summary>
    /// Tags associated with this fix (e.g. "online", "bypass", "hypervisor", "refix").
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// File size in bytes, if provided by the catalog.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Optional description or notes for the fix.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional direct download URL or endpoint reference.
    /// </summary>
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Formatted tags representation (e.g. "BYPASS + HYPERVISOR").
    /// </summary>
    public string TagsSummary => Tags.Count > 0
        ? string.Join(" + ", Tags.Select(t => t.ToUpperInvariant()))
        : "CUSTOM FIX";

    /// <summary>
    /// Indicates whether this fix acts as a DRM / license bypass.
    /// </summary>
    public bool IsBypass => Tags.Any(t => string.Equals(t, "bypass", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Indicates whether this fix utilizes a hypervisor / VM-level emulation layer.
    /// </summary>
    public bool IsHypervisor => Tags.Any(t => string.Equals(t, "hypervisor", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Indicates whether this fix provides online/Steamworks multiplayer capabilities.
    /// </summary>
    public bool IsOnline => Tags.Any(t =>
        string.Equals(t, "online", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t, "onlinefix", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Community positive upvotes count.
    /// </summary>
    public int PositiveVotes { get; init; }

    /// <summary>
    /// Community negative downvotes count.
    /// </summary>
    public int NegativeVotes { get; init; }

    /// <summary>
    /// Total community votes recorded.
    /// </summary>
    public int TotalVotes => PositiveVotes + NegativeVotes;

    /// <summary>
    /// Indicates whether there are enough community votes to display a percentage score.
    /// </summary>
    public bool HasEnoughVotesForScore => TotalVotes >= 5;

    /// <summary>
    /// Positive score percentage (0 - 100%).
    /// </summary>
    public double ScorePercentage => TotalVotes > 0
        ? (double)PositiveVotes / TotalVotes * 100.0
        : 0.0;

    /// <summary>
    /// Indicates whether the local user has already cast a vote for this fix.
    /// </summary>
    public bool HasUserVoted { get; init; }

    /// <summary>
    /// Formatted human-readable file size.
    /// </summary>
    public string FormattedSize => SizeBytes switch
    {
        > 1024 * 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SizeBytes.Value / (1024.0 * 1024.0 * 1024.0):F2} GB"),
        > 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SizeBytes.Value / (1024.0 * 1024.0):F1} MB"),
        > 0 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SizeBytes.Value / 1024.0:F0} KB"),
        _ => "ZIP Archive"
    };
}
