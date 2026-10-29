using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Parsed parameters of a Nexus Mods nxm:// link.
/// Format: nxm://{GameDomain}/mods/{ModId}/files/{FileId}?key={Key}&amp;expires={Expires}&amp;user_id={UserId}
/// </summary>
public sealed record NexusNxmLink
{
    public required string GameDomain { get; init; }
    public required long ModId { get; init; }
    public required long FileId { get; init; }
    public required string Key { get; init; }
    public required long Expires { get; init; }
    public long? UserId { get; init; }
}

/// <summary>
/// Metadata of a mod on Nexus Mods.
/// </summary>
public sealed record NexusModInfo
{
    public required long ModId { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? PictureUrl { get; init; }
    public required string GameDomain { get; init; }
    public long TotalDownloads { get; init; }
    public long EndorsementCount { get; init; }
}

/// <summary>
/// Metadata of a downloadable file on Nexus Mods.
/// </summary>
public sealed record NexusFileInfo
{
    public required long FileId { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public long SizeInBytes { get; init; }
    public string? FileName { get; init; }
    public string? Description { get; init; }
}
