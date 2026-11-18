using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Gets the admin preset this recommendation came from, or null for community curation.
    /// </summary>
    public string? PresetId { get; init; }

    /// <summary>
    /// Gets whether the preset also brings mods or a custom emulator, deployed after the download.
    /// </summary>
    public bool HasPresetExtras { get; init; }

    /// <summary>ReFix catalog mods the preset picks (null = ReFix decides, empty = none).</summary>
    public IReadOnlyList<string>? ReFixModIds { get; init; }

    /// <summary>
    /// The depots to download for this recommendation, pinned to its manifests. An admin preset is a whole
    /// build: base-game depots it does not pin are left out, so an old build is not mixed with current
    /// content (CS:GO 2018 has no depots 735-738). DLC depots and community curation keep the old behaviour.
    /// </summary>
    public IReadOnlyList<DepotInfo> SelectDepots(IEnumerable<DepotInfo> depots, IReadOnlySet<uint> baseGameDepotIds)
    {
        var exclusive = PresetId != null && PinnedManifests.Count > 0;
        return depots
            .Where(d => !exclusive || PinnedManifests.ContainsKey(d.DepotId) || !baseGameDepotIds.Contains(d.DepotId))
            .Select(d => PinnedManifests.TryGetValue(d.DepotId, out var pinned) && pinned > 0
                ? d with { ManifestId = pinned, IsDownloaded = false }
                : d)
            .ToList();
    }
}
