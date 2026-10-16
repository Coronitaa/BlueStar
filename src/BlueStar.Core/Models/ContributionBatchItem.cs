using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a candidate manifest revision in a voluntary community contribution batch preview.
/// Strictly enforces privacy guarantees: contains zero user credentials, tokens, session cookies, or SteamIDs.
/// </summary>
public sealed record ContributionBatchItem
{
    /// <summary>
    /// Friendly title of the game or DLC.
    /// </summary>
    public string AppName { get; init; } = string.Empty;

    /// <summary>
    /// Steam Application identifier.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Steam Depot identifier.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Manifest revision identifier (GID).
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Source provenance badge (e.g., "ManifestHub2", "K25", "DepotBox", "LocalCache").
    /// </summary>
    public string Provenance { get; init; } = "Community";

    /// <summary>
    /// Size of the manifest binary in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets formatted human-readable size string.
    /// </summary>
    public string SizeFormatted => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "Unknown size";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }
        return $"{size:0.##} {suffixes[suffixIndex]}";
    }
}
