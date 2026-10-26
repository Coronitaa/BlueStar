using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// A game community hosted on Thunderstore (e.g. Lethal Company, Valheim).
/// </summary>
public sealed record ThunderstoreCommunity
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public string? DiscordUrl { get; init; }
    public string? WikiUrl { get; init; }
    public int Packages { get; init; }
    public long DownloadCount { get; init; }
}

/// <summary>
/// A package version on Thunderstore.
/// </summary>
public sealed record ThunderstorePackageVersion
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string VersionNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public int Downloads { get; init; }
    public DateTimeOffset DateCreated { get; init; }
    public string? WebsiteUrl { get; init; }
    public bool IsActive { get; init; } = true;
    public long FileSize { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>
/// A mod package listing on Thunderstore with its available versions and metadata.
/// </summary>
public sealed record ThunderstorePackage
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Owner { get; init; }
    public required string PackageUrl { get; init; }
    public string? DonationLink { get; init; }
    public DateTimeOffset DateCreated { get; init; }
    public DateTimeOffset DateUpdated { get; init; }
    public int RatingScore { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDeprecated { get; init; }
    public long TotalDownloads { get; init; }
    public bool HasNsfwContent { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<ThunderstorePackageVersion> Versions { get; init; } = [];

    /// <summary>The most recent version of this package.</summary>
    public ThunderstorePackageVersion? LatestVersion => Versions.Count > 0 ? Versions[0] : null;

    /// <summary>Summary description from the latest version.</summary>
    public string Description => LatestVersion?.Description ?? string.Empty;

    /// <summary>Icon URL from the latest version.</summary>
    public string IconUrl => LatestVersion?.IconUrl ?? string.Empty;
}
