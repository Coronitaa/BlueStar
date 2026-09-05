using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides a persistent, global local cache of validated Steam .manifest files
/// shared across all game instances.
/// </summary>
public interface IManifestCacheService
{
    /// <summary>
    /// Checks whether a valid manifest artifact is present in the local cache.
    /// </summary>
    bool HasManifest(uint depotId, ulong manifestId);

    /// <summary>
    /// Gets the absolute local file path for a cached manifest, or null if not cached.
    /// </summary>
    string? GetManifestPath(uint depotId, ulong manifestId);

    /// <summary>
    /// Stores a manifest stream into the persistent cache and validates its integrity.
    /// </summary>
    Task<string> StoreManifestAsync(uint depotId, ulong manifestId, Stream sourceStream, CancellationToken ct = default);

    /// <summary>
    /// Copies an existing manifest file into the persistent cache and validates its integrity.
    /// </summary>
    Task<string> StoreManifestFileAsync(uint depotId, ulong manifestId, string sourceFilePath, CancellationToken ct = default);

    /// <summary>
    /// Validates the binary header magic and integrity of a manifest file.
    /// </summary>
    bool ValidateManifest(string filePath);
}
