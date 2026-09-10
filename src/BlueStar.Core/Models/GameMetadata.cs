using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents metadata about a specific game.
/// </summary>
public record GameMetadata
{
    /// <summary>
    /// Gets or sets the application identifier for the game.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets or sets the name of the game.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the developer of the game.
    /// </summary>
    public string? Developer { get; init; }

    /// <summary>
    /// Gets or sets the publisher of the game.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>
    /// Gets or sets the description of the game.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets the URL to the header image.
    /// </summary>
    public string? HeaderImageUrl { get; init; }

    /// <summary>
    /// Gets or sets the URL to the capsule image.
    /// </summary>
    public string? CapsuleImageUrl { get; init; }

    /// <summary>
    /// Gets or sets the release date as a string.
    /// </summary>
    public string? ReleaseDate { get; init; }

    /// <summary>
    /// Gets or sets the list of categories for the game.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// Gets or sets the list of genres for the game.
    /// </summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>
    /// Gets the long "About this game" copy from the Steam store page, HTML stripped.
    /// <see cref="Description"/> stays the one-line summary.
    /// </summary>
    public string? AboutTheGame { get; init; }

    /// <summary>
    /// Gets the store screenshot URLs, in Steam's own order.
    /// </summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>
    /// Gets the developer's website, when the store page lists one.
    /// </summary>
    public string? Website { get; init; }

    /// <summary>
    /// Gets the Metacritic score, or null when the game has none.
    /// </summary>
    public int? MetacriticScore { get; init; }

    /// <summary>
    /// Gets the platforms the store page reports (e.g. "Windows", "macOS", "Linux").
    /// </summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>
    /// Gets the community tags shown on the store page ("Roguelike", "Pixel Graphics", …), most
    /// applied first. These are distinct from <see cref="Genres"/> (the publisher's own
    /// classification) and <see cref="Categories"/> (Steam features such as Single-player), which
    /// is why the three are surfaced as separate groups.
    /// </summary>
    public IReadOnlyList<string> StoreTags { get; init; } = [];

    /// <summary>
    /// Gets or sets the date and time when the metadata was last updated.
    /// </summary>
    public DateTimeOffset LastUpdated { get; init; }
}
