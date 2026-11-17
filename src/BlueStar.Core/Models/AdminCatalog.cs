using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// The admin-curated catalog published on the BlueStar admin panel (GitHub Pages).
/// Every instance of these records has already passed <c>AdminCatalogParser</c>: URLs are https and
/// allowlisted or checksum-pinned, ids are sane and paths cannot leave the game folder.
/// </summary>
public sealed record AdminCatalog
{
    public static readonly AdminCatalog Empty = new();

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<AdminCatalogGame> Games { get; init; } = [];
}

/// <summary>A game the admin added to Explore, hid from it, or attached install presets to.</summary>
public sealed record AdminCatalogGame
{
    public uint AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public string? HeaderImageUrl { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<AdminInstallPreset> Presets { get; init; } = [];
}

/// <summary>A recommended way to install a game: a pinned build, an emulator, mods and a note.</summary>
public sealed record AdminInstallPreset
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Branch { get; init; } = "public";
    public string? BuildId { get; init; }
    public IReadOnlyDictionary<uint, ulong> Manifests { get; init; } = new Dictionary<uint, ulong>();
    public AdminEmulator Emulator { get; init; } = new();
    public IReadOnlyList<AdminDownload> Mods { get; init; } = [];
    public string? Note { get; init; }

    /// <summary>Maps the preset onto the recommendation the install modal already knows how to apply.</summary>
    public CurationRecommendation ToRecommendation(uint appId) => new()
    {
        AppId = appId,
        RecommendedBuildId = BuildId ?? string.Empty,
        RecommendedBranch = Branch,
        Summary = Name,
        PinnedManifests = Manifests,
        RecommendedEmulator = Emulator.Type switch
        {
            AdminEmulatorType.ReFix => "ReFix",
            AdminEmulatorType.ReFixGoldberg => "ReFix Goldberg",
            AdminEmulatorType.OnlineFix => "OnlineFix",
            // A custom emulator is laid down by the preset deployer, not by BlueStar's own emulator.
            _ => "none"
        },
        CompatibilityNotes = Note,
        Source = "BlueStar",
        PresetId = Id,
        HasPresetExtras = Emulator.Type == AdminEmulatorType.Custom || Mods.Count > 0
    };
}

public enum AdminEmulatorType
{
    None,
    ReFix,
    ReFixGoldberg,
    OnlineFix,
    Custom
}

/// <summary>The emulator a preset installs. Only <see cref="AdminEmulatorType.Custom"/> uses the download fields.</summary>
public sealed record AdminEmulator
{
    public AdminEmulatorType Type { get; init; } = AdminEmulatorType.None;
    public string? Name { get; init; }
    public AdminDownload? Download { get; init; }

    /// <summary>Game-relative INI file the settings are written to (custom emulators only).</summary>
    public string? SettingsFile { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>A file BlueStar downloads for a preset: a mod or a custom emulator archive.</summary>
public sealed record AdminDownload
{
    public string Name { get; init; } = string.Empty;
    public Uri Url { get; init; } = new("https://invalid.invalid/");

    /// <summary>Lowercase hex SHA-256, or null when the host is allowlisted and no checksum was given.</summary>
    public string? Sha256 { get; init; }
}
