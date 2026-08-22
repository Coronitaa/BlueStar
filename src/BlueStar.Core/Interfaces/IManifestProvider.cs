using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to retrieve and download game manifests.
/// </summary>
public interface IManifestProvider
{
    /// <summary>
    /// Retrieves the list of manifests for a given application.
    /// </summary>
    /// <param name="appId">The application identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of manifests.</returns>
    Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct);

    /// <summary>
    /// Downloads a specific manifest file.
    /// </summary>
    /// <param name="depotId">The depot identifier associated with the manifest.</param>
    /// <param name="manifestId">The manifest identifier.</param>
    /// <param name="targetPath">The directory path where the manifest should be saved.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the file path of the downloaded manifest.</returns>
    Task<string> DownloadManifestAsync(uint depotId, ulong manifestId, string targetPath, CancellationToken ct);
}
