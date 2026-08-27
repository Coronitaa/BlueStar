using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Result of dispatching a mod payload via heuristics.
/// </summary>
public record ModDispatchResult
{
    public required bool Success { get; init; }
    public string? DestinationPath { get; init; }
    public string? TargetCategory { get; init; }
    public int FilesDispatched { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Heuristic mod linker and dispatcher that inspects mod payload contents (.pak, .vpk, .dll, .json)
/// and routes files to engine-specific mod folders (~mods, addons, BepInEx/plugins, profile dirs).
/// </summary>
public interface IHeuristicModDispatcher
{
    /// <summary>
    /// Analyzes the files inside a mod source directory or archive and links/deploys them
    /// into the proper game instance destination folder.
    /// </summary>
    Task<ModDispatchResult> DispatchModPayloadAsync(
        string modSourcePath,
        string instancePath,
        EngineInfo? engineInfo = null,
        string? modName = null,
        CancellationToken ct = default);
}
