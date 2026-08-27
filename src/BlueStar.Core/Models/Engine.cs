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
/// Distinguishes Unity scripting backend implementations.
/// </summary>
public enum UnityFlavor
{
    None,
    Mono,
    IL2CPP
}

/// <summary>
/// Identifies the processor architecture of the game binaries.
/// </summary>
public enum TargetArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}

/// <summary>
/// Identifies the recommended mod loader for the detected game engine.
/// </summary>
public enum RecommendedModLoader
{
    None,
    BepInEx5_x64,
    BepInEx5_x86,
    BepInEx6_IL2CPP_x64,
    UE4SS_x64,
    ProxyDll
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
    UE4SSSupported = 1 << 7,
    All = Mods | Emulation | SteamIntegration | CustomFiles | LaunchArguments | BepInExSupported | WorkshopSupported | UE4SSSupported
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
    /// Unity scripting backend flavor (Mono vs IL2CPP) if Unity.
    /// </summary>
    public UnityFlavor UnityFlavor { get; init; } = UnityFlavor.None;

    /// <summary>
    /// Detected binary target architecture.
    /// </summary>
    public TargetArchitecture Architecture { get; init; } = TargetArchitecture.Unknown;

    /// <summary>
    /// Recommended mod loader for this engine instance.
    /// </summary>
    public RecommendedModLoader RecommendedLoader { get; init; } = RecommendedModLoader.None;

    /// <summary>
    /// Capabilities supported for this engine instance.
    /// </summary>
    public EngineCapabilities Capabilities { get; init; } = EngineCapabilities.None;

    /// <summary>
    /// Helper to check if a capability is supported.
    /// </summary>
    public bool Supports(EngineCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>
    /// Formatted display text (e.g. "Unreal Engine 4.27", "Unity (Mono x64)", or "Unity").
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (Type == EngineType.Unity && UnityFlavor != UnityFlavor.None)
            {
                var flavorStr = UnityFlavor == UnityFlavor.IL2CPP ? "IL2CPP" : "Mono";
                var archStr = Architecture == TargetArchitecture.X86 ? "x86" : "x64";
                return !string.IsNullOrWhiteSpace(Version)
                    ? $"{Name} {Version} ({flavorStr} {archStr})"
                    : $"{Name} ({flavorStr} {archStr})";
            }

            return !string.IsNullOrWhiteSpace(Version)
                ? $"{Name} {Version}"
                : Name;
        }
    }
}
