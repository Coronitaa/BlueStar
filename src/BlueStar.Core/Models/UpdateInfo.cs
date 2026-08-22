using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents information about an available update.
/// </summary>
public record UpdateInfo
{
    /// <summary>
    /// Gets or sets the version string of the update.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets or sets the URL from which the update can be downloaded.
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// Gets or sets the release notes for the update.
    /// </summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Gets or sets the date and time when the update was published.
    /// </summary>
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>
    /// Gets or sets the total file size of the update in bytes.
    /// </summary>
    public long FileSize { get; init; }
}
