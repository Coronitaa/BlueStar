using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// A category/tag on CurseForge for a specific game.
/// </summary>
public sealed record CurseForgeCategory
{
    public required int Id { get; init; }
    public required int GameId { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? IconUrl { get; init; }
}

/// <summary>
/// A downloadable file of a mod on CurseForge.
/// </summary>
public sealed record CurseForgeFile
{
    public required int Id { get; init; }
    public required int GameId { get; init; }
    public required int ModId { get; init; }
    public required string DisplayName { get; init; }
    public required string FileName { get; init; }
    public long FileLength { get; init; }
    public long DownloadCount { get; init; }
    public DateTimeOffset FileDate { get; init; }
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// A mod listing on CurseForge.
/// </summary>
public sealed record CurseForgeMod
{
    public required int Id { get; init; }
    public required int GameId { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? PrimaryAuthor { get; init; }
    public long DownloadCount { get; init; }
    public DateTimeOffset DateModified { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<CurseForgeFile> Files { get; init; } = [];

    public CurseForgeFile? LatestFile => Files.Count > 0 ? Files[0] : null;
}

/// <summary>
/// Result of a CurseForge search query including pagination total count.
/// </summary>
public sealed record CurseForgeSearchResult(IReadOnlyList<CurseForgeMod> Mods, int TotalCount);
