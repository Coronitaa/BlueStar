using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// How sure the placement resolver is about where a mod's files belong.
/// Low-confidence plans are shown to the user before anything is written.
/// </summary>
public enum PlacementConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Layout hints a provider knows about its packages, so the resolver does not have to guess.
/// </summary>
public enum ModPackageLayout
{
    /// <summary>Unknown layout: the resolver inspects the files.</summary>
    Unknown,

    /// <summary>Thunderstore/r2modman BepInEx package: plugins/, patchers/, config/ map into BepInEx/.</summary>
    BepInExPackage,

    /// <summary>Archive mirrors the game folder and extracts at the install root.</summary>
    GameRoot,

    /// <summary>
    /// Steam Workshop item content, laid out exactly as the author uploaded it. Steam itself only ever puts it in
    /// a per-item folder and the game asks ISteamUGC where that is, so the item goes where the Steam emulator
    /// serves Workshop items from (steam_settings/mods/&lt;id&gt;) when there is one.
    /// </summary>
    SteamWorkshop
}

/// <summary>One file to copy: absolute path in the staging folder to a path relative to the install folder.</summary>
public sealed record ModFileOperation(string SourcePath, string RelativeDestination);

/// <summary>
/// Where a mod's files will go, decided before anything is written.
/// </summary>
public sealed record ModInstallPlan
{
    /// <summary>Short name of the rule that produced the plan, e.g. "RootAligned" or "UnrealPaks".</summary>
    public required string Strategy { get; init; }
    public required PlacementConfidence Confidence { get; init; }
    public required IReadOnlyList<ModFileOperation> Operations { get; init; }

    /// <summary>Human-readable reason, shown when the user is asked to confirm.</summary>
    public string? Reason { get; init; }
}

/// <summary>Extra knowledge passed to the resolver.</summary>
public sealed record ModPlacementHints
{
    public string? ModName { get; init; }
    public ModPackageLayout Layout { get; init; } = ModPackageLayout.Unknown;

    /// <summary>PublishedFileId of a Steam Workshop item (required for <see cref="ModPackageLayout.SteamWorkshop"/>).</summary>
    public ulong? WorkshopItemId { get; init; }

    public static ModPlacementHints None { get; } = new();
}
