using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service to manage Windows Defender exclusions for game directories and emulators.
/// </summary>
public interface IWindowsDefenderService
{
    /// <summary>
    /// Adds a folder exclusion in Windows Defender.
    /// </summary>
    Task<bool> AddFolderExclusionAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Removes a folder exclusion from Windows Defender.
    /// </summary>
    Task<bool> RemoveFolderExclusionAsync(string folderPath, CancellationToken ct = default);
}
