using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents an active emulator or fix layer installed in a game instance.
/// Enables simultaneous composability of multiple fixes (e.g., BYPASS + HYPERVISOR + ONLINEFIX or BYPASS + REFIX).
/// </summary>
public record FixLayerInfo
{
    /// <summary>
    /// Unique identifier for this installed layer (e.g., "depotbox_fix_007_bypass", "refix_valve").
    /// </summary>
    public required string LayerId { get; init; }

    /// <summary>
    /// The original Fix ID from DepotBox or provider, if applicable.
    /// </summary>
    public string? FixId { get; init; }

    /// <summary>
    /// Type/Provider of this fix layer ("depotbox_gamefix", "refix", "smokeapi", "custom").
    /// </summary>
    public required string SourceType { get; init; }

    /// <summary>
    /// Friendly display name of this layer.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Tags describing this layer (e.g. "bypass", "hypervisor", "online").
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Relative paths of all files deployed/copied to the game directory by this fix.
    /// </summary>
    public IReadOnlyList<string> DeployedFiles { get; init; } = [];

    /// <summary>
    /// Relative or absolute path where original overwritten files were backed up for clean rollback.
    /// </summary>
    public string? BackupDirectory { get; init; }

    /// <summary>
    /// Version string of the deployed fix.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Timestamp when this layer was installed.
    /// </summary>
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Formatted tags representation (e.g. "BYPASS + HYPERVISOR").
    /// </summary>
    public string TagsSummary => Tags.Count > 0
        ? string.Join(" + ", Tags.Select(t => t.ToUpperInvariant()))
        : (SourceType.ToUpperInvariant());

    /// <summary>
    /// Indicates whether this layer acts as a bypass.
    /// </summary>
    public bool IsBypass => Tags.Any(t => string.Equals(t, "bypass", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Indicates whether this layer acts as a hypervisor.
    /// </summary>
    public bool IsHypervisor => Tags.Any(t => string.Equals(t, "hypervisor", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Indicates whether this layer provides online functionality.
    /// </summary>
    public bool IsOnline => Tags.Any(t =>
        string.Equals(t, "online", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t, "onlinefix", StringComparison.OrdinalIgnoreCase));
}
