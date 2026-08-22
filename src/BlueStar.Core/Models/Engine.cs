using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Identifies the game engine family or ecosystem.
/// </summary>
public enum EngineType
{
    Generic,
    UnrealEngine,
    Unity,
    Godot,
    Source,
    Source2,
    Klei,
    CreationEngine,
    ParadoxClausewitz,
    ReEngine,
    MtFramework,
    RedEngine,
    Frostbite,
    Decima,
    IdTech,
    CryEngine,
    Supergiant,
    RpgMaker,
    RenPy,
    GameMaker,
    Custom
}

/// <summary>
/// Bitwise flags declaring capabilities supported by a game engine or instance.
/// </summary>
[Flags]
public enum EngineCapabilities
{
    None = 0,
    Mods = 1 << 0,
    Emulation = 1 << 1,
    SteamIntegration = 1 << 2,
    CustomFiles = 1 << 3,
    LaunchArguments = 1 << 4,
    BepInExSupported = 1 << 5,
    WorkshopSupported = 1 << 6,
    All = Mods | Emulation | SteamIntegration | CustomFiles | LaunchArguments | BepInExSupported | WorkshopSupported
}

/// <summary>
/// First-class model representing the game engine associated with an instance.
/// </summary>
public record EngineInfo
{
    /// <summary>
    /// Unique identifier for the engine (e.g., "unreal-4", "unity", "godot", "generic").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the engine (e.g., "Unreal Engine 4", "Unity", "Godot Engine").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Engine type classification.
    /// </summary>
    public EngineType Type { get; init; } = EngineType.Generic;

    /// <summary>
    /// Specific version string if detected (e.g., "4.27", "2022.3 LTS", "4.3").
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Capabilities supported for this engine instance.
    /// </summary>
    public EngineCapabilities Capabilities { get; init; } = EngineCapabilities.None;

    /// <summary>
    /// Helper to check if a capability is supported.
    /// </summary>
    public bool Supports(EngineCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>
    /// Formatted display text (e.g. "Unreal Engine 4.27" or "Unity").
    /// </summary>
    public string DisplayText => !string.IsNullOrWhiteSpace(Version)
        ? $"{Name} {Version}"
        : Name;
}
