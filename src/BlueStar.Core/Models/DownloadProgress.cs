using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the progress of an ongoing download operation.
/// </summary>
public record DownloadProgress
{
    /// <summary>
    /// Gets or sets the total number of bytes to download.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets or sets the number of bytes that have been downloaded so far.
    /// </summary>
    public long DownloadedBytes { get; init; }

    /// <summary>
    /// Gets or sets the percentage of the download that is complete (0 to 100).
    /// </summary>
    public double Percentage { get; init; }

    /// <summary>
    /// Gets or sets the name of the file currently being downloaded.
    /// </summary>
    public string? CurrentFile { get; init; }

    /// <summary>
    /// Gets or sets the current download speed in bytes per second.
    /// </summary>
    public double Speed { get; init; }

    /// <summary>
    /// Gets or sets the estimated time remaining for the download to complete.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}
