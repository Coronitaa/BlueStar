using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service responsible for detecting the game engine and its capabilities from the installation folder.
/// </summary>
public interface IEngineDetector
{
    /// <summary>
    /// Detects the game engine, version, and capabilities from a directory.
    /// </summary>
    /// <param name="installPath">The game installation directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected EngineInfo, or a generic fallback if not recognized.</returns>
    Task<EngineInfo> DetectEngineAsync(string installPath, CancellationToken ct = default);

    /// <summary>
    /// Finds the primary executable path for the detected game engine.
    /// </summary>
    /// <param name="installPath">The game installation directory.</param>
    /// <param name="gameName">Optional game name hint.</param>
    /// <returns>Path to the executable or null if not found.</returns>
    string? FindPrimaryExecutable(string installPath, string? gameName = null);
}
