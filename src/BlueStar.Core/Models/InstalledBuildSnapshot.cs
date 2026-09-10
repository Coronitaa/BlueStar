using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// A point-in-time record of a build that was installed on an instance.
/// <para>
/// Depot manifests survive an update on disk — the update flow writes each one as
/// <c>{depotId}_{manifestId}.manifest</c> under the instance manifests folder and never removes the
/// previous ones — but nothing recorded WHICH set of manifests made up a given build, so rolling
/// back was impossible. This snapshot is that missing record.
/// </para>
/// </summary>
public record InstalledBuildSnapshot
{
    /// <summary>
    /// Gets the build identifier this snapshot describes (e.g. "15961492").
    /// </summary>
    public string BuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the branch the build came from (e.g. "public").
    /// </summary>
    public string BranchName { get; init; } = "public";

    /// <summary>
    /// Gets an optional human label for the build, shown in the rollback list.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the DepotId -> ManifestId map that reconstitutes this build. This is what a rollback
    /// re-applies, and what is checked against the manifest cache to know whether it is still
    /// recoverable.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong> ManifestMap { get; init; } = new Dictionary<uint, ulong>();

    /// <summary>
    /// Gets when this build was installed on the instance.
    /// </summary>
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the publication date of the build itself, when known.
    /// </summary>
    public DateTimeOffset? BuildDate { get; init; }

    /// <summary>
    /// Gets the total on-disk size the build occupied, in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets a formatted size for display.
    /// </summary>
    public string FormattedSize => SizeBytes > 0
        ? $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : "—";

    /// <summary>
    /// Gets the build date formatted for display, falling back to the install date.
    /// </summary>
    public string FormattedDate => (BuildDate ?? InstalledAt).ToString("d MMM yyyy");

    /// <summary>Gets the headline shown on a saved-build chip.</summary>
    public string PresetTitle => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : $"Build {BuildId}";

    /// <summary>Gets the second line of a saved-build chip: id, depot count and size.</summary>
    public string PresetDetail
    {
        get
        {
            var depots = ManifestMap.Count;
            var size = SizeBytes > 0 ? $" · {FormattedSize}" : string.Empty;
            return $"{BuildId} · {depots} depot{(depots == 1 ? "" : "s")}{size}";
        }
    }
}
