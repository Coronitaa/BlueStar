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
    public string? CapsuleImageUrl { get; init; }
    public string? HeroImageUrl { get; init; }
    public string? LogoImageUrl { get; init; }
    public string? Description { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Release date as unix seconds.</summary>
    public long? ReleaseDateUtc { get; init; }

    /// <summary>Steam store tag ids, so Explore's tag filters find the game.</summary>
    public IReadOnlyList<int> TagIds { get; init; } = [];

    /// <summary>Depot decryption keys (hex) for the depots the presets pin, so old builds can be downloaded.</summary>
    public IReadOnlyDictionary<uint, string> DepotKeys { get; init; } = new Dictionary<uint, string>();

    public IReadOnlyList<AdminInstallPreset> Presets { get; init; } = [];

    /// <summary>The row Explore's local catalog needs for the game to be part of the feed.</summary>
    public CatalogAppItem ToCatalogItem() => new()
    {
        AppId = AppId,
        Name = Name,
        NormalizedName = Helpers.DeterministicNormalizer.Normalize(Name),
        CompactName = Helpers.DeterministicNormalizer.ToCompactKey(Name),
        AppType = "game",
        HeaderImageUrl = HeaderImageUrl,
        ReleaseDateUtc = ReleaseDateUtc,
        TagIds = TagIds,
        IsEnriched = false
    };
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

    /// <summary>
    /// ReFix catalog mod ids (refix_mods/catalog.tsv) the ReFix deploy installs. Null leaves it to ReFix
    /// (the first catalog mod that matches the game); empty installs none.
    /// </summary>
    public IReadOnlyList<string>? ReFixMods { get; init; }

    public string? Note { get; init; }

    /// <summary>Name and note in other languages, keyed by two-letter code ("en", "es").</summary>
    public IReadOnlyDictionary<string, AdminPresetText> Translations { get; init; } = new Dictionary<string, AdminPresetText>();

    /// <summary>Name in the UI language when the catalog has it, else <see cref="Name"/>.</summary>
    public string DisplayName => Translated()?.Name ?? Name;

    /// <summary>Note in the UI language when the catalog has it, else <see cref="Note"/>.</summary>
    public string? DisplayNote => Translated()?.Note ?? Note;

    private AdminPresetText? Translated()
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Translations.TryGetValue(lang, out var t) ? t : null;
    }

    /// <summary>Maps the preset onto the recommendation the install modal already knows how to apply.</summary>
    public CurationRecommendation ToRecommendation(uint appId) => new()
    {
        AppId = appId,
        RecommendedBuildId = BuildId ?? string.Empty,
        RecommendedBranch = Branch,
        Summary = DisplayName,
        PinnedManifests = Manifests,
        RecommendedEmulator = Emulator.Type switch
        {
            // The install modal's own option ids, so the choice maps one to one.
            AdminEmulatorType.ReFixLan => "refix_goldberg_offline",
            AdminEmulatorType.ReFixLanOnline => "refix_goldberg",
            AdminEmulatorType.ReFixOnlineLegacy => "refix_valve",
            AdminEmulatorType.OnlineFix => "onlinefix",
            // A custom emulator is laid down by the preset deployer, not by BlueStar's own emulator.
            _ => "none"
        },
        CompatibilityNotes = DisplayNote,
        Source = "BlueStar",
        PresetId = Id,
        ReFixModIds = ReFixMods,
        HasPresetExtras = Emulator.Type == AdminEmulatorType.Custom || Mods.Count > 0
    };
}

/// <summary>A preset's name and note in one language.</summary>
public sealed record AdminPresetText
{
    public string? Name { get; init; }
    public string? Note { get; init; }
}

public enum AdminEmulatorType
{
    None,
    /// <summary>ReFix LAN (offline).</summary>
    ReFixLan,
    /// <summary>ReFix Mixed (LAN + Online).</summary>
    ReFixLanOnline,
    /// <summary>ReFix Online Legacy (Steam Spacewar 480).</summary>
    ReFixOnlineLegacy,
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
