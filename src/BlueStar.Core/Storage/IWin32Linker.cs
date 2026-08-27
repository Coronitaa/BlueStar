using System;

namespace BlueStar.Core.Storage;

/// <summary>
/// Low-level filesystem manager interface for creating and managing Win32 Hardlinks,
/// Directory Junctions (NTFS Reparse Points), and Symbolic Links.
/// </summary>
public interface IWin32Linker
{
    /// <summary>
    /// Creates an NTFS hardlink between a new link path and an existing file.
    /// </summary>
    /// <param name="linkPath">The new hardlink file path to create.</param>
    /// <param name="targetPath">The existing target file path.</param>
    /// <returns>True if created successfully; otherwise, false.</returns>
    bool CreateHardLink(string linkPath, string targetPath);

    /// <summary>
    /// Creates a symbolic link pointing to a target file or directory.
    /// </summary>
    /// <param name="linkPath">The symbolic link path to create.</param>
    /// <param name="targetPath">The target file or directory path.</param>
    /// <param name="isDirectory">True if target is a directory; false if file.</param>
    /// <returns>True if created successfully; otherwise, false.</returns>
    bool CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory);

    /// <summary>
    /// Creates an NTFS Directory Junction (Reparse Point with IO_REPARSE_TAG_MOUNT_POINT).
    /// </summary>
    /// <param name="junctionPath">The directory path where the junction should be created.</param>
    /// <param name="targetDirectory">The target directory path the junction points to.</param>
    /// <returns>True if junction was created successfully; otherwise, false.</returns>
    bool CreateJunction(string junctionPath, string targetDirectory);

    /// <summary>
    /// Determines whether the specified directory is an NTFS Junction / Reparse Point.
    /// </summary>
    /// <param name="path">The directory path to check.</param>
    /// <returns>True if the path is a junction; otherwise, false.</returns>
    bool IsJunction(string path);

    /// <summary>
    /// Gets the target directory path that a junction points to.
    /// </summary>
    /// <param name="junctionPath">The junction directory path.</param>
    /// <returns>The target path, or null if not a junction.</returns>
    string? GetJunctionTarget(string junctionPath);

    /// <summary>
    /// Safely deletes a directory junction without deleting or affecting the target folder contents.
    /// </summary>
    /// <param name="junctionPath">The junction directory path to delete.</param>
    /// <returns>True if deleted successfully; otherwise, false.</returns>
    bool DeleteJunction(string junctionPath);

    /// <summary>
    /// Gets the hard link count (nNumberOfLinks) for a file via Win32 GetFileInformationByHandle.
    /// Returns 1 for normal unlinked files, and > 1 if hardlinked.
    /// </summary>
    /// <param name="filePath">The file path to inspect.</param>
    /// <returns>Number of hard links pointing to this file's inode/MFT entry.</returns>
    int GetFileLinkCount(string filePath);

    /// <summary>
    /// Retrieves the volume file system name (e.g. "NTFS", "exFAT", "FAT32", "ReFS") for a path.
    /// </summary>
    /// <param name="path">Path on the target volume.</param>
    /// <returns>File system name string.</returns>
    string GetVolumeFileSystem(string path);

    /// <summary>
    /// Checks if the volume hosting the path supports NTFS hardlinks.
    /// </summary>
    bool SupportsHardLinks(string path);

    /// <summary>
    /// Checks if the volume hosting the path supports NTFS reparse points / junctions.
    /// </summary>
    bool SupportsReparsePoints(string path);

    /// <summary>
    /// Checks if two paths reside on the same drive volume (required for hardlinks).
    /// </summary>
    bool IsSameVolume(string pathA, string pathB);
}
