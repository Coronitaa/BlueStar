using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BlueStar.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Production-grade low-level Win32 filesystem linker for creating NTFS hardlinks,
/// directory junctions via FSCTL_SET_REPARSE_POINT, and symbolic links with fallback.
/// </summary>
public sealed class Win32Linker : IWin32Linker
{
    private readonly ILogger<Win32Linker> _logger;

    public Win32Linker(ILogger<Win32Linker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Win32 Native Constants & P/Invoke

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;

    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    private const uint SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;
    private const uint SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkW(
        string lpSymlinkFileName,
        string lpTargetFileName,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct REPARSE_DATA_BUFFER_HEADER
    {
        public uint ReparseTag;
        public ushort ReparseDataLength;
        public ushort Reserved;
    }

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    #endregion

    /// <inheritdoc />
    public bool CreateHardLink(string linkPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath)) throw new ArgumentNullException(nameof(linkPath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentNullException(nameof(targetPath));

        if (!File.Exists(targetPath))
        {
            _logger.LogWarning("Cannot create hardlink: target file does not exist: {Target}", targetPath);
            return false;
        }

        var linkDir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(linkDir))
        {
            Directory.CreateDirectory(linkDir);
        }

        if (File.Exists(linkPath))
        {
            try { File.Delete(linkPath); } catch { }
        }

        if (!IsSameVolume(linkPath, targetPath))
        {
            _logger.LogWarning("Cannot create hardlink across different volumes: '{Link}' and '{Target}'. Falling back to copy.", linkPath, targetPath);
            return false;
        }

        bool success = CreateHardLinkW(linkPath, targetPath, IntPtr.Zero);
        if (!success)
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogWarning("CreateHardLinkW failed for '{Link}' -> '{Target}'. Win32 Error: {Error}", linkPath, targetPath, err);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(linkPath)) throw new ArgumentNullException(nameof(linkPath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentNullException(nameof(targetPath));

        var linkDir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(linkDir))
        {
            Directory.CreateDirectory(linkDir);
        }

        uint flags = 0;
        if (isDirectory) flags |= SYMBOLIC_LINK_FLAG_DIRECTORY;
        flags |= SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;

        bool success = CreateSymbolicLinkW(linkPath, targetPath, flags);
        if (!success)
        {
            // Try without unprivileged create flag for older Windows builds
            flags = isDirectory ? SYMBOLIC_LINK_FLAG_DIRECTORY : 0;
            success = CreateSymbolicLinkW(linkPath, targetPath, flags);
        }

        if (!success)
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogWarning("CreateSymbolicLinkW failed for '{Link}' -> '{Target}'. Win32 Error: {Error}", linkPath, targetPath, err);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool CreateJunction(string junctionPath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(junctionPath)) throw new ArgumentNullException(nameof(junctionPath));
        if (string.IsNullOrWhiteSpace(targetDirectory)) throw new ArgumentNullException(nameof(targetDirectory));

        if (!Directory.Exists(targetDirectory))
        {
            _logger.LogWarning("Cannot create junction: Target directory does not exist: {Target}", targetDirectory);
            return false;
        }

        var fullTarget = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullJunction = Path.GetFullPath(junctionPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Ensure parent of junction exists
        var parentDir = Path.GetDirectoryName(fullJunction);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        // If junction already exists as a reparse point, delete junction first
        if (Directory.Exists(fullJunction) || File.Exists(fullJunction))
        {
            if (IsJunction(fullJunction))
            {
                DeleteJunction(fullJunction);
            }
            else
            {
                // Must be an empty directory for junction creation
                var subDirs = Directory.GetFileSystemEntries(fullJunction);
                if (subDirs.Length > 0)
                {
                    _logger.LogWarning("Cannot create junction at '{Junction}': Directory is not empty.", fullJunction);
                    return false;
                }
            }
        }
        else
        {
            Directory.CreateDirectory(fullJunction);
        }

        // Build native NT target path: \??\C:\Path\To\Target
        string substituteName = @"\??\" + fullTarget;
        string printName = fullTarget;

        byte[] substituteNameBytes = Encoding.Unicode.GetBytes(substituteName);
        byte[] printNameBytes = Encoding.Unicode.GetBytes(printName);

        // Header: ReparseTag(4) + ReparseDataLength(2) + Reserved(2) = 8 bytes
        // MountPoint: SubstituteNameOffset(2) + SubstituteNameLength(2) + PrintNameOffset(2) + PrintNameLength(2) = 8 bytes
        // PathBuffer: substitute bytes + 2 null bytes + print bytes + 2 null bytes
        int pathBufferLength = substituteNameBytes.Length + 2 + printNameBytes.Length + 2;
        int totalDataLength = 8 + pathBufferLength;
        int totalBufferSize = 8 + totalDataLength;

        byte[] buffer = new byte[totalBufferSize];
        using var ms = new MemoryStream(buffer);
        using var writer = new BinaryWriter(ms);

        // REPARSE_DATA_BUFFER_HEADER
        writer.Write(IO_REPARSE_TAG_MOUNT_POINT); // ReparseTag (0xA0000003)
        writer.Write((ushort)totalDataLength);     // ReparseDataLength
        writer.Write((ushort)0);                   // Reserved

        // MountPointReparseBuffer
        writer.Write((ushort)0);                                       // SubstituteNameOffset
        writer.Write((ushort)substituteNameBytes.Length);              // SubstituteNameLength
        writer.Write((ushort)(substituteNameBytes.Length + 2));        // PrintNameOffset
        writer.Write((ushort)printNameBytes.Length);                   // PrintNameLength

        // PathBuffer
        writer.Write(substituteNameBytes);
        writer.Write((ushort)0); // Null terminator
        writer.Write(printNameBytes);
        writer.Write((ushort)0); // Null terminator

        IntPtr hDir = CreateFileW(
            fullJunction,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (hDir == INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogWarning("Failed to open directory handle for junction '{Junction}'. Win32 Error: {Error}", fullJunction, err);
            return false;
        }

        try
        {
            IntPtr pBuffer = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, pBuffer, buffer.Length);
                bool success = DeviceIoControl(
                    hDir,
                    FSCTL_SET_REPARSE_POINT,
                    pBuffer,
                    (uint)buffer.Length,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);

                if (!success)
                {
                    int err = Marshal.GetLastWin32Error();
                    _logger.LogWarning("FSCTL_SET_REPARSE_POINT failed for '{Junction}' -> '{Target}'. Win32 Error: {Error}", fullJunction, fullTarget, err);
                    return false;
                }

                _logger.LogDebug("Created NTFS Junction: '{Junction}' -> '{Target}'", fullJunction, fullTarget);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pBuffer);
            }
        }
        finally
        {
            CloseHandle(hDir);
        }
    }

    /// <inheritdoc />
    public bool IsJunction(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
            return false;

        try
        {
            var di = new DirectoryInfo(path);
            if ((di.Attributes & FileAttributes.ReparsePoint) == 0)
                return false;

            // Inspect LinkTarget or reparse tag
            if (!string.IsNullOrEmpty(di.LinkTarget))
                return true;

            IntPtr hDir = CreateFileW(
                path,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (hDir == INVALID_HANDLE_VALUE)
                return (di.Attributes & FileAttributes.ReparsePoint) != 0;

            try
            {
                byte[] outBuffer = new byte[1024];
                IntPtr pOut = Marshal.AllocHGlobal(outBuffer.Length);
                try
                {
                    bool result = DeviceIoControl(
                        hDir,
                        FSCTL_GET_REPARSE_POINT,
                        IntPtr.Zero,
                        0,
                        pOut,
                        (uint)outBuffer.Length,
                        out uint bytesReturned,
                        IntPtr.Zero);

                    if (result && bytesReturned >= 4)
                    {
                        uint tag = (uint)Marshal.ReadInt32(pOut);
                        return tag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pOut);
                }
            }
            finally
            {
                CloseHandle(hDir);
            }

            return (di.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? GetJunctionTarget(string junctionPath)
    {
        if (!IsJunction(junctionPath)) return null;

        try
        {
            var di = new DirectoryInfo(junctionPath);
            if (!string.IsNullOrEmpty(di.LinkTarget))
            {
                return di.LinkTarget;
            }

            IntPtr hDir = CreateFileW(
                junctionPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (hDir == INVALID_HANDLE_VALUE) return null;

            try
            {
                byte[] outBuffer = new byte[16384];
                IntPtr pOut = Marshal.AllocHGlobal(outBuffer.Length);
                try
                {
                    bool result = DeviceIoControl(
                        hDir,
                        FSCTL_GET_REPARSE_POINT,
                        IntPtr.Zero,
                        0,
                        pOut,
                        (uint)outBuffer.Length,
                        out uint bytesReturned,
                        IntPtr.Zero);

                    if (result && bytesReturned >= 16)
                    {
                        uint tag = (uint)Marshal.ReadInt32(pOut);
                        if (tag == IO_REPARSE_TAG_MOUNT_POINT)
                        {
                            ushort subNameOffset = (ushort)Marshal.ReadInt16(pOut, 8);
                            ushort subNameLen = (ushort)Marshal.ReadInt16(pOut, 10);
                            ushort printNameOffset = (ushort)Marshal.ReadInt16(pOut, 12);
                            ushort printNameLen = (ushort)Marshal.ReadInt16(pOut, 14);

                            int pathBufferStart = 16;
                            if (printNameLen > 0)
                            {
                                return Marshal.PtrToStringUni(new IntPtr(pOut.ToInt64() + pathBufferStart + printNameOffset), printNameLen / 2);
                            }
                            if (subNameLen > 0)
                            {
                                var sub = Marshal.PtrToStringUni(new IntPtr(pOut.ToInt64() + pathBufferStart + subNameOffset), subNameLen / 2);
                                if (sub != null && sub.StartsWith(@"\??\"))
                                    return sub.Substring(4);
                                return sub;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pOut);
                }
            }
            finally
            {
                CloseHandle(hDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read junction target for {Path}", junctionPath);
        }

        return null;
    }

    /// <inheritdoc />
    public bool DeleteJunction(string junctionPath)
    {
        if (string.IsNullOrWhiteSpace(junctionPath)) return false;

        try
        {
            if (!IsJunction(junctionPath))
            {
                if (Directory.Exists(junctionPath))
                {
                    Directory.Delete(junctionPath);
                    return true;
                }
                return false;
            }

            // Remove reparse point via DeviceIoControl
            IntPtr hDir = CreateFileW(
                junctionPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (hDir != INVALID_HANDLE_VALUE)
            {
                try
                {
                    var header = new REPARSE_DATA_BUFFER_HEADER
                    {
                        ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                        ReparseDataLength = 0,
                        Reserved = 0
                    };

                    IntPtr pHeader = Marshal.AllocHGlobal(Marshal.SizeOf(header));
                    try
                    {
                        Marshal.StructureToPtr(header, pHeader, false);
                        DeviceIoControl(
                            hDir,
                            FSCTL_DELETE_REPARSE_POINT,
                            pHeader,
                            (uint)Marshal.SizeOf(header),
                            IntPtr.Zero,
                            0,
                            out _,
                            IntPtr.Zero);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pHeader);
                    }
                }
                finally
                {
                    CloseHandle(hDir);
                }
            }

            // Delete directory without recursion to ensure safety
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete junction '{Path}'", junctionPath);
            return false;
        }
    }

    /// <inheritdoc />
    public int GetFileLinkCount(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return 0;

        IntPtr hFile = CreateFileW(
            filePath,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (hFile == INVALID_HANDLE_VALUE)
        {
            return 1;
        }

        try
        {
            if (GetFileInformationByHandle(hFile, out var info))
            {
                return (int)info.nNumberOfLinks;
            }
            return 1;
        }
        finally
        {
            CloseHandle(hFile);
        }
    }

    /// <inheritdoc />
    public string GetVolumeFileSystem(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Unknown";

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "Unknown";

            var fsBuffer = new StringBuilder(260);
            if (GetVolumeInformationW(root, null, 0, out _, out _, out _, fsBuffer, (uint)fsBuffer.Capacity))
            {
                return fsBuffer.ToString();
            }
        }
        catch { }

        return "Unknown";
    }

    /// <inheritdoc />
    public bool SupportsHardLinks(string path)
    {
        var fs = GetVolumeFileSystem(path);
        return string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool SupportsReparsePoints(string path)
    {
        var fs = GetVolumeFileSystem(path);
        return string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool IsSameVolume(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;

        var rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
        var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));

        return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }
}
