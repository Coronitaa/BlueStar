using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a managed game instance.
/// </summary>
public record GameInstance
{
    /// <summary>
    /// Gets or sets the unique identifier for the game instance.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the name of the game.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the application identifier for the game.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets or sets the installation path of the game.
    /// </summary>
    public required string InstallPath { get; init; }

    /// <summary>
    /// Gets or sets the schema version of the instance. Default is 1.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Gets or sets the date and time when the instance was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the date and time when the instance was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the list of depots associated with the game instance.
    /// </summary>
    public IReadOnlyList<DepotInfo> Depots { get; init; } = [];

    /// <summary>
    /// Gets or sets the list of DLCs associated with the game instance.
    /// </summary>
    public IReadOnlyList<DlcInfo> Dlcs { get; init; } = [];

    /// <summary>
    /// Gets or sets the metadata associated with the game.
    /// </summary>
    public GameMetadata? Metadata { get; init; }

    /// <summary>
    /// Gets the current status of the game instance.
    /// </summary>
    public InstanceStatus Status { get; init; } = InstanceStatus.NotInstalled;

    /// <summary>
    /// Gets or sets the path to the original DepotBox ZIP archive, if imported from one.
    /// Used to extract .manifest files before downloading.
    /// </summary>
    public string? SourceArchivePath { get; init; }

    /// <summary>
    /// Gets or sets the detected or configured game engine for this instance.
    /// </summary>
    public EngineInfo? Engine { get; init; }

    /// <summary>
    /// Gets or sets the relative or absolute path to the main game executable.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets custom launch command line arguments for the game.
    /// </summary>
    public string? LaunchArguments { get; init; }

    /// <summary>
    /// Gets or sets the date and time when the game was last played.
    /// </summary>
    public DateTimeOffset? LastPlayedAt { get; init; }

    /// <summary>
    /// Gets or sets the accumulated play time for this instance.
    /// </summary>
    public TimeSpan TotalPlayTime { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets whether an emulator is enabled for this instance.
    /// </summary>
    public bool EmulatorEnabled { get; init; }

    /// <summary>
    /// Gets or sets the ID of the configured emulator (e.g., "refix", "smokeapi").
    /// </summary>
    public string? EmulatorId { get; init; }

    /// <summary>
    /// Gets or sets the deployed version of the emulator (e.g., "1.0", "1.1").
    /// </summary>
    public string? InstalledEmulatorVersion { get; init; }

    /// <summary>
    /// Gets or sets whether a game update / newer build is available for this instance.
    /// </summary>
    public bool HasUpdateAvailable { get; init; }

    /// <summary>
    /// Gets or sets details about the available update.
    /// </summary>
    public string? UpdateDescription { get; init; }

    /// <summary>
    /// Gets or sets whether automatic update checking is disabled for this instance.
    /// When true, BlueStar will not query Steam/DepotBox for newer builds for this game,
    /// will not raise <see cref="HasUpdateAvailable"/>, and will not show update badges.
    /// The user can still trigger a manual check from the instance detail view.
    /// </summary>
    public bool DisableUpdateChecks { get; init; } = false;

    /// <summary>
    /// Gets or sets the origin and management type of this instance.
    /// Default is DepotBox for backward compatibility with pre-1.3 instances.
    /// </summary>
    public InstanceOrigin Origin { get; init; } = InstanceOrigin.DepotBox;

    /// <summary>
    /// Gets or sets the build identifier currently active/installed on this instance (e.g. "15961492").
    /// </summary>
    public string? ActiveBuildId { get; init; }

    /// <summary>
    /// Gets or sets the branch currently active/installed on this instance (e.g. "public").
    /// </summary>
    public string? ActiveBranch { get; init; }

    /// <summary>
    /// Gets or sets the map of DepotId -> installed ManifestId for differential update detection.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong> InstalledManifestMap { get; init; } = new Dictionary<uint, ulong>();

    /// <summary>
    /// Gets or sets the release or creation date of the currently installed build/patch.
    /// </summary>
    public DateTimeOffset? InstalledVersionDate { get; init; }


    /// <summary>
    /// Gets or sets whether an imported folder instance has been successfully associated with a manifest provider.
    /// </summary>
    public bool IsDepotBoxAssociated { get; init; } = false;

    /// <summary>
    /// Gets or sets whether a DLC unlocker (SmokeAPI/CreamAPI) is installed for this instance.
    /// </summary>
    public bool DlcUnlockerInstalled { get; init; } = false;

    /// <summary>
    /// Gets or sets the list of DLC AppIDs that are actively unlocked.
    /// </summary>
    public IReadOnlyList<uint> UnlockedDlcIds { get; init; } = [];

    /// <summary>
    /// Gets or sets the builds this instance has had installed, newest first.
    /// Populated by the update flow right before <see cref="InstalledManifestMap"/> is overwritten,
    /// and used to offer a rollback to a previous version from cached manifests.
    /// Capped to the most recent few entries.
    /// </summary>
    public IReadOnlyList<InstalledBuildSnapshot> BuildHistory { get; init; } = [];

    /// <summary>
    /// Gets or sets the custom manifest configurations the user saved for this game — the
    /// "Save without downloading" presets shown as chips in the Version tab's Custom mode.
    /// Newest first.
    /// </summary>
    public IReadOnlyList<InstalledBuildSnapshot> SavedCustomBuilds { get; init; } = [];

    /// <summary>
    /// Gets or sets whether the installed build is pinned to a specific version and should not be
    /// offered for update (set by the "Recommended" and "Previous version" download modes).
    /// </summary>
    public bool IsBuildPinned { get; init; } = false;

    /// <summary>
    /// Gets or sets whether this instance is mid-way through a game update and still has to have its
    /// emulator / DLC unlocker redeployed once the new depot files finish downloading.
    /// Set by the update flow before the download is enqueued, cleared once the redeploy succeeds.
    /// </summary>
    public bool AwaitingPostUpdateRedeploy { get; init; } = false;

    /// <summary>
    /// Gets or sets the emulator option id that must be redeployed after the pending game update
    /// completes (null when no emulator was installed before the update).
    /// </summary>
    public string? PendingRedeployEmulatorId { get; init; }

    /// <summary>
    /// Gets or sets whether the DLC unlocker must be reinstalled after the pending game update completes.
    /// </summary>
    public bool PendingRedeployDlcUnlocker { get; init; } = false;

    /// <summary>
    /// Gets or sets the fix layer ids that must be redeployed after the pending game update completes.
    /// </summary>
    public IReadOnlyList<string> PendingRedeployFixLayerIds { get; init; } = [];

    /// <summary>
    /// Gets or sets the active fix and emulator layers installed on this instance.
    /// Supports multi-layer composition (e.g. BYPASS + HYPERVISOR + ONLINEFIX).
    /// </summary>
    public IReadOnlyList<FixLayerInfo> InstalledFixLayers { get; init; } = [];

    /// <summary>
    /// Gets whether this instance was imported from a local Steam installation.
    /// </summary>
    public bool IsSteamGame => Origin == InstanceOrigin.Steam;

    /// <summary>
    /// Gets whether this instance was imported from a folder.
    /// </summary>
    public bool IsImportedFolder => Origin == InstanceOrigin.ImportedFolder;

    /// <summary>
    /// Gets whether depot files and manifest downloads can be managed for this instance.
    /// </summary>
    public bool CanManageDepots => Origin != InstanceOrigin.Steam;

    /// <summary>
    /// Gets the Steam header image URL for this game instance.
    /// </summary>
    public string HeaderImageUrl => !string.IsNullOrWhiteSpace(Metadata?.HeaderImageUrl)
        ? Metadata.HeaderImageUrl
        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
}

