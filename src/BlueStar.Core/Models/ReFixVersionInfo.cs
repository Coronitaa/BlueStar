using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Information about a ReFix emulator release from GitHub.
/// </summary>
public sealed class ReFixVersionInfo
{
    /// <summary>
    /// SemVer or tag version string (e.g. "1.1").
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Raw release tag from GitHub (e.g. "v1.1").
    /// </summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>
    /// Release notes / changelog description.
    /// </summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>
    /// Direct download URL for the deployment ZIP package (e.g. ReFix_Release_v1.1.zip).
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// Size of the download asset in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// When the release was published on GitHub.
    /// </summary>
    public DateTimeOffset PublishedAt { get; init; } = DateTimeOffset.UtcNow;
}
