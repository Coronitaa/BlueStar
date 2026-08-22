using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to parse and extract information from DepotBox archives.
/// </summary>
public interface IDepotBoxArchiveParser
{
    /// <summary>
    /// Parses a DepotBox ZIP file.
    /// </summary>
    /// <param name="zipFilePath">The file path to the ZIP archive.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the parsed archive information.</returns>
    Task<DepotBoxArchive> ParseAsync(string zipFilePath, CancellationToken ct);

    /// <summary>
    /// Parses the content of a Lua script representing DepotBox definitions.
    /// </summary>
    /// <param name="luaContent">The contents of the Lua script.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the parsed archive information.</returns>
    Task<DepotBoxArchive> ParseLuaAsync(string luaContent, CancellationToken ct);

    /// <summary>
    /// Extracts manifest files from a DepotBox ZIP file.
    /// </summary>
    /// <param name="zipFilePath">The file path to the ZIP archive.</param>
    /// <param name="targetDirectory">The directory where the manifests should be extracted.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of file paths to the extracted manifests.</returns>
    Task<IReadOnlyList<string>> ExtractManifestsAsync(string zipFilePath, string targetDirectory, CancellationToken ct);
}
