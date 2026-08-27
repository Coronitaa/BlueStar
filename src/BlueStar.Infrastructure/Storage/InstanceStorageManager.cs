using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Production-ready zero-copy instance storage manager implementing NTFS Hardlinks,
/// Directory Junctions, Copy-on-Write (CoW) link breaking, and non-NTFS fallback.
/// </summary>
public sealed class InstanceStorageManager : IInstanceStorageManager
{
    private readonly IWin32Linker _linker;
    private readonly ILogger<InstanceStorageManager> _logger;

    public InstanceStorageManager(IWin32Linker linker, ILogger<InstanceStorageManager> logger)
    {
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<InstanceDeployResult> CreateInstanceAsync(
        string depotPath,
        string instancePath,
        InstanceDeployOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(depotPath)) throw new ArgumentNullException(nameof(depotPath));
        if (string.IsNullOrWhiteSpace(instancePath)) throw new ArgumentNullException(nameof(instancePath));

        options ??= new InstanceDeployOptions();
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(depotPath))
        {
            return new InstanceDeployResult
            {
                Success = false,
                ErrorMessage = $"Base depot directory does not exist: '{depotPath}'",
                Duration = sw.Elapsed
            };
        }

        var fullDepot = Path.GetFullPath(depotPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullInstance = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Directory.CreateDirectory(fullInstance);

        // Check NTFS & Same Volume
        bool supportsHardlinks = _linker.SupportsHardLinks(fullInstance);
        bool sameVolume = _linker.IsSameVolume(fullDepot, fullInstance);

        if (!supportsHardlinks || !sameVolume)
        {
            string reason = !supportsHardlinks
                ? $"Target filesystem ({_linker.GetVolumeFileSystem(fullInstance)}) does not support NTFS hardlinks."
                : "Source depot and target instance are on different drive volumes.";

            if (!options.AllowNonNtfsFallback)
            {
                return new InstanceDeployResult
                {
                    Success = false,
                    IsZeroCopy = false,
                    FallbackReason = reason,
                    ErrorMessage = $"Zero-copy deployment failed: {reason}",
                    Duration = sw.Elapsed
                };
            }

            _logger.LogWarning("Zero-copy deployment unavailable ({Reason}). Falling back to standard physical copy.", reason);
            var copyResult = await FallbackCopyDirectoryAsync(fullDepot, fullInstance, ct).ConfigureAwait(false);
            sw.Stop();

            return new InstanceDeployResult
            {
                Success = true,
                TotalFilesCopied = copyResult.FileCount,
                TotalFilesLinked = 0,
                TotalJunctionsCreated = 0,
                IsZeroCopy = false,
                FallbackReason = reason,
                Duration = sw.Elapsed
            };
        }

        // Zero-Copy NTFS Deployment
        long linkedFiles = 0;
        long copiedFiles = 0;
        long junctionsCreated = 0;

        var isolatedSet = new HashSet<string>(
            options.IsolatedFolderNames.Select(NormalizeRelativePath),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. Traverse all directories and files from depot
            var depotDi = new DirectoryInfo(fullDepot);
            var allDirectories = depotDi.GetDirectories("*", SearchOption.AllDirectories);

            // Create corresponding local directories in instance
            foreach (var dir in allDirectories)
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullDepot, dir.FullName));

                // Always create real local folder in instance
                var targetSubDir = Path.Combine(fullInstance, relativePath);
                Directory.CreateDirectory(targetSubDir);
            }

            // Also ensure all configured isolated directories exist locally
            foreach (var isoDir in options.IsolatedFolderNames)
            {
                var targetIso = Path.Combine(fullInstance, isoDir.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetIso);
            }

            // 2. Link or copy files
            var allFiles = depotDi.GetFiles("*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                var relFilePath = Path.GetRelativePath(fullDepot, file.FullName);
                var targetFilePath = Path.Combine(fullInstance, relFilePath);
                var relParentDir = NormalizeRelativePath(Path.GetDirectoryName(relFilePath) ?? string.Empty);

                // Check if file is inside an isolated mutable folder
                bool isIsolated = isolatedSet.Any(iso => relParentDir.Equals(iso, StringComparison.OrdinalIgnoreCase) ||
                                                         relParentDir.StartsWith(iso + "/", StringComparison.OrdinalIgnoreCase));

                if (isIsolated)
                {
                    // Copy as dedicated mutable file (link count 1)
                    var targetDir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    file.CopyTo(targetFilePath, overwrite: true);
                    copiedFiles++;
                }
                else
                {
                    // Create NTFS Hardlink for read-only binary/asset/executable
                    bool linked = _linker.CreateHardLink(targetFilePath, file.FullName);
                    if (linked)
                    {
                        linkedFiles++;
                    }
                    else
                    {
                        // Fallback to copy for this single file if hardlink failed
                        var targetDir = Path.GetDirectoryName(targetFilePath);
                        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                        file.CopyTo(targetFilePath, overwrite: true);
                        copiedFiles++;
                    }
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "Zero-copy instance deployed at {Path} in {Ms}ms. (Hardlinks: {Linked}, Copied: {Copied}, Junctions: {Junctions})",
                fullInstance, sw.ElapsedMilliseconds, linkedFiles, copiedFiles, junctionsCreated);

            return new InstanceDeployResult
            {
                Success = true,
                TotalFilesLinked = linkedFiles,
                TotalFilesCopied = copiedFiles,
                TotalJunctionsCreated = junctionsCreated,
                IsZeroCopy = true,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Failed to deploy zero-copy instance at {Path}", fullInstance);
            return new InstanceDeployResult
            {
                Success = false,
                IsZeroCopy = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    /// <inheritdoc />
    public async Task<bool> BreakLinkAndCopyAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            int linkCount = _linker.GetFileLinkCount(filePath);
            if (linkCount <= 1)
            {
                // File is already unique and not hardlinked
                return true;
            }

            _logger.LogDebug("Copy-on-Write triggered for hardlinked file {Path} (LinkCount={Count})", filePath, linkCount);

            var tempCopy = Path.Combine(Path.GetTempPath(), $"bluestar_cow_{Guid.NewGuid():N}.tmp");
            try
            {
                // Read all bytes to temp copy
                await using (var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var dst = new FileStream(tempCopy, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                // Delete hardlink from instance
                File.Delete(filePath);

                // Move independent physical copy into place
                File.Move(tempCopy, filePath);

                _logger.LogInformation("Successfully broke hardlink (CoW) for {Path}. File is now independent.", filePath);
                return true;
            }
            finally
            {
                if (File.Exists(tempCopy))
                {
                    try { File.Delete(tempCopy); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to break hardlink for file {Path}", filePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureCoWFileAsync(string filePath, string? sourceDepotFilePath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        if (!File.Exists(filePath))
        {
            if (!string.IsNullOrWhiteSpace(sourceDepotFilePath) && File.Exists(sourceDepotFilePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(sourceDepotFilePath, filePath, overwrite: true);
                return true;
            }
            return false;
        }

        return await BreakLinkAndCopyAsync(filePath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> DeleteInstanceAsync(string instancePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            return Task.FromResult(false);

        try
        {
            var fullInstance = Path.GetFullPath(instancePath);

            // 1. Remove any directory junctions safely first
            var subDirs = Directory.GetDirectories(fullInstance, "*", SearchOption.AllDirectories);
            foreach (var dir in subDirs)
            {
                if (_linker.IsJunction(dir))
                {
                    _linker.DeleteJunction(dir);
                }
            }

            if (_linker.IsJunction(fullInstance))
            {
                _linker.DeleteJunction(fullInstance);
                return Task.FromResult(true);
            }

            // 2. Delete instance directory (unlinks all hardlinks without affecting base depot)
            Directory.Delete(fullInstance, recursive: true);
            _logger.LogInformation("Safely deleted instance directory at {Path}", fullInstance);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete instance at {Path}", instancePath);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<InstanceStorageStats> GetStorageStatsAsync(string instancePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return Task.FromResult(new InstanceStorageStats());
        }

        try
        {
            long sharedBytes = 0;
            long uniqueBytes = 0;
            int hardlinkedCount = 0;
            int uniqueCount = 0;
            int junctionCount = 0;

            var fullPath = Path.GetFullPath(instancePath);
            var di = new DirectoryInfo(fullPath);

            foreach (var dir in di.GetDirectories("*", SearchOption.AllDirectories))
            {
                if (_linker.IsJunction(dir.FullName))
                {
                    junctionCount++;
                }
            }

            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                int links = _linker.GetFileLinkCount(file.FullName);
                if (links > 1)
                {
                    sharedBytes += file.Length;
                    hardlinkedCount++;
                }
                else
                {
                    uniqueBytes += file.Length;
                    uniqueCount++;
                }
            }

            return Task.FromResult(new InstanceStorageStats
            {
                SharedHardlinkedBytes = sharedBytes,
                UniqueAllocatedBytes = uniqueBytes,
                TotalApparentBytes = sharedBytes + uniqueBytes,
                HardlinkedFileCount = hardlinkedCount,
                UniqueFileCount = uniqueCount,
                JunctionCount = junctionCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate storage stats for {Path}", instancePath);
            return Task.FromResult(new InstanceStorageStats());
        }
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', '/')
            .Trim('/');
    }

    private async Task<(long FileCount, long TotalBytes)> FallbackCopyDirectoryAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        long count = 0;
        long bytes = 0;

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, rel));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, rel);

            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            var fi = new FileInfo(file);
            bytes += fi.Length;
            count++;

            await using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            await srcStream.CopyToAsync(dstStream, ct).ConfigureAwait(false);
        }

        return (count, bytes);
    }
}
