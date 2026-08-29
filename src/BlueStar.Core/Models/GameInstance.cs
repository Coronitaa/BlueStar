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
    /// Gets or sets the origin and management type of this instance.
    /// </summary>
    public InstanceOrigin Origin { get; init; } = InstanceOrigin.DepotBox;

    /// <summary>
    /// Gets or sets whether an imported folder instance has been successfully associated with DepotBox.
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
    /// Gets whether this instance was imported from a local Steam installation.
    /// </summary>
    public bool IsSteamGame => Origin == InstanceOrigin.Steam;

    /// <summary>
    /// Gets whether this instance was imported from a folder.
    /// </summary>
    public bool IsImportedFolder => Origin == InstanceOrigin.ImportedFolder;

    /// <summary>
    /// Gets the Steam header image URL for this game instance.
    /// </summary>
    public string HeaderImageUrl => !string.IsNullOrWhiteSpace(Metadata?.HeaderImageUrl)
        ? Metadata.HeaderImageUrl
        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
}
