using System.Runtime.InteropServices;
using BlueStar.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Linux implementation of <see cref="IWin32Linker"/>. Hard links through libc <c>link()</c>; junctions do not exist
/// on Linux, so directory symlinks stand in for them (Wine follows them like real folders).
/// </summary>
public sealed class UnixLinker : IWin32Linker
{
    // Filesystems without POSIX links: deploys there fall back to copying, as on FAT drives on Windows.
    private static readonly string[] NoLinkFileSystems = ["vfat", "msdos", "exfat"];

    private readonly ILogger<UnixLinker> _logger;

    public UnixLinker(ILogger<UnixLinker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string oldPath, string newPath);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(string path, byte[] buf);

    /// <summary>The linker for the OS BlueStar is running on.</summary>
    public static IWin32Linker ForCurrentOs(ILoggerFactory loggers) => OperatingSystem.IsWindows()
        ? new Win32Linker(loggers.CreateLogger<Win32Linker>())
        : new UnixLinker(loggers.CreateLogger<UnixLinker>());

    public bool CreateHardLink(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!File.Exists(targetPath))
        {
            _logger.LogWarning("Cannot create hardlink: target file does not exist: {Target}", targetPath);
            return false;
        }

        var linkDir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(linkDir))
            Directory.CreateDirectory(linkDir);

        if (File.Exists(linkPath))
        {
            try { File.Delete(linkPath); } catch (IOException) { }
        }

        if (Link(targetPath, linkPath) == 0)
            return true;

        _logger.LogWarning("link() failed for '{Link}' -> '{Target}'. errno {Errno}", linkPath, targetPath, Marshal.GetLastPInvokeError());
        return false;
    }

    public bool CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        try
        {
            var linkDir = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(linkDir))
                Directory.CreateDirectory(linkDir);

            if (isDirectory)
                Directory.CreateSymbolicLink(linkPath, targetPath);
            else
                File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "symlink failed for '{Link}' -> '{Target}'", linkPath, targetPath);
            return false;
        }
    }

    public bool CreateJunction(string junctionPath, string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(junctionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!Directory.Exists(targetDirectory))
        {
            _logger.LogWarning("Cannot create junction: Target directory does not exist: {Target}", targetDirectory);
            return false;
        }

        var fullJunction = Path.GetFullPath(junctionPath).TrimEnd(Path.DirectorySeparatorChar);
        if (IsJunction(fullJunction))
        {
            DeleteJunction(fullJunction);
        }
        else if (Directory.Exists(fullJunction))
        {
            // Same contract as an NTFS junction: only an empty folder may be replaced.
            if (Directory.EnumerateFileSystemEntries(fullJunction).Any())
            {
                _logger.LogWarning("Cannot create junction at '{Junction}': Directory is not empty.", fullJunction);
                return false;
            }
            Directory.Delete(fullJunction);
        }

        return CreateSymbolicLink(fullJunction, Path.GetFullPath(targetDirectory), isDirectory: true);
    }

    public bool IsJunction(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return new DirectoryInfo(path) is { Exists: true, LinkTarget: not null };
        }
        catch (IOException)
        {
            return false;
        }
    }

    public string? GetJunctionTarget(string junctionPath)
    {
        if (!IsJunction(junctionPath)) return null;
        var target = new DirectoryInfo(junctionPath).LinkTarget!;
        return Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(junctionPath))!);
    }

    public bool DeleteJunction(string junctionPath)
    {
        if (string.IsNullOrWhiteSpace(junctionPath)) return false;
        try
        {
            if (IsJunction(junctionPath))
            {
                File.Delete(junctionPath); // unlink(): removes the link, never the target's contents
                return true;
            }

            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete junction '{Path}'", junctionPath);
            return false;
        }
    }

    public int GetFileLinkCount(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return 0;

        // shortcut: st_nlink offset 16 is the x86-64 glibc struct stat layout; other architectures report 1
        // until BlueStar ships for them.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return 1;

        var buf = new byte[256];
        try
        {
            return Stat(filePath, buf) == 0 ? (int)BitConverter.ToUInt64(buf, 16) : 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1; // glibc < 2.33 exports stat only as __xstat
        }
    }

    public string GetVolumeFileSystem(string path)
    {
        try
        {
            return FindMount(path)?.DriveFormat ?? "Unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    public bool SupportsHardLinks(string path) =>
        !NoLinkFileSystems.Contains(GetVolumeFileSystem(path), StringComparer.OrdinalIgnoreCase);

    public bool SupportsReparsePoints(string path) => SupportsHardLinks(path);

    public bool IsSameVolume(string pathA, string pathB)
    {
        var a = FindMount(pathA);
        var b = FindMount(pathB);
        return a != null && b != null && a.Name == b.Name;
    }

    /// <summary>The mount holding <paramref name="path"/>: the longest mount point that prefixes it.</summary>
    private static DriveInfo? FindMount(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => IsUnder(full, d.Name))
                .MaxBy(d => d.Name.Length);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string mount)
    {
        var root = mount.TrimEnd('/');
        return root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
