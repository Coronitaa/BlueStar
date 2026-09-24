using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service responsible for catalog snapshot distribution, version checking, and atomic local ingestion.
/// </summary>
public interface ICatalogSnapshotService
{
    /// <summary>
    /// Gets the current locally installed catalog version, or 0 if unversioned.
    /// </summary>
    Task<int> GetCurrentVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks a remote manifest URL for catalog snapshot updates. If a newer version is found,
    /// downloads the snapshot, verifies its SHA256 checksum, imports it atomically into the repository,
    /// and updates the local version state.
    /// </summary>
    Task<bool> CheckAndUpdateSnapshotAsync(string manifestUrl, CancellationToken ct = default);
}
