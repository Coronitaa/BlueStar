using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a parsed DepotBox archive containing game definitions.
/// </summary>
public record DepotBoxArchive
{
    /// <summary>
    /// Gets or sets the primary application identifier associated with the archive.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets or sets the list of games contained in the archive.
    /// </summary>
    public IReadOnlyList<DepotBoxGame> Games { get; init; } = [];
}

/// <summary>
/// Represents a game entry within a DepotBox archive.
/// </summary>
public record DepotBoxGame
{
    /// <summary>
    /// Gets or sets the application identifier for the game or DLC.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets or sets the depot decryption key for the game.
    /// </summary>
    public required string DepotKey { get; init; }

    /// <summary>
    /// Gets or sets the flag associated with the game.
    /// </summary>
    public int Flag { get; init; }

    /// <summary>
    /// Gets or sets the list of depots required by the game.
    /// </summary>
    public IReadOnlyList<DepotBoxDepot> Depots { get; init; } = [];

    /// <summary>
    /// Gets or sets the name of the game or DLC.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry is downloadable content (DLC).
    /// </summary>
    public bool IsDlc { get; init; }
}

/// <summary>
/// Represents a depot entry within a DepotBox archive.
/// </summary>
public record DepotBoxDepot
{
    /// <summary>
    /// Gets or sets the identifier for the depot.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets or sets the manifest identifier for the depot.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets or sets the size of the depot in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets or sets the file name of the manifest, if applicable.
    /// </summary>
    public string? ManifestFileName { get; init; }

    /// <summary>
    /// Gets or sets the name of the depot.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or sets the category of the depot ("Base Game" or "DLC").
    /// </summary>
    public string Category { get; init; } = "Base Game";

    /// <summary>
    /// Gets or sets the target platform ("Windows", "Linux", "macOS", "Universal").
    /// </summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    /// Gets or sets the target architecture ("64-bit", "32-bit", or null).
    /// </summary>
    public string? Architecture { get; init; }
}
