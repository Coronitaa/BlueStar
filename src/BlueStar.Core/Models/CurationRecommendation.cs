using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a curated or community-tested recommendation for a game.
/// Advises which build is the most stable, which manifests are tested,
/// and which emulators or fixes work reliably.
/// </summary>
public record CurationRecommendation
{
    /// <summary>
    /// Gets the Steam AppID.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets the recommended build ID.
    /// </summary>
    public string RecommendedBuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the recommended branch name (default: "public").
    /// </summary>
    public string RecommendedBranch { get; init; } = "public";

    /// <summary>
    /// Gets a headline or summary of why this build is recommended.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets specific depot-to-manifest pin overrides tested to work without crashes.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong> PinnedManifests { get; init; } = new Dictionary<uint, ulong>();

    /// <summary>
    /// Gets the recommended emulator type ("Goldberg", "Spacewar480", "SmokeAPI", "None").
    /// </summary>
    public string? RecommendedEmulator { get; init; }

    /// <summary>
    /// Gets any special compatibility flags or launch arguments.
    /// </summary>
    public string? CompatibilityNotes { get; init; }

    /// <summary>
    /// Gets the timestamp when this curation was last verified.
    /// </summary>
    public DateTimeOffset CuratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the source of the recommendation ("CuratedDatabase", "CommunityVotes", "SteamPublicLatest").
    /// </summary>
    public string Source { get; init; } = "CuratedDatabase";
}
