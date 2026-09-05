using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Cache;

/// <summary>
/// Implements persistent, global local caching and validation for Steam depot .manifest files.
/// Manifests are keyed by canonical identity: DepotId_ManifestId.
/// </summary>
public sealed class ManifestCacheService : IManifestCacheService
{
    private readonly string _cacheRoot;
    private readonly ILogger<ManifestCacheService> _logger;

    // Steam Protobuf Manifest Magic: 0x71F617D0 (Little-endian bytes: 0xD0, 0x17, 0xF6, 0x71)
    private const uint SteamProtobufManifestMagic = 0x71F617D0;

    // Gzip Magic: 0x8B1F
    private const ushort GzipMagic = 0x8B1F;

    public ManifestCacheService(
        ILogger<ManifestCacheService> logger,
        string? cacheRoot = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "cache", "manifests");

        try
        {
            Directory.CreateDirectory(_cacheRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create manifest cache directory at {Path}", _cacheRoot);
        }
    }

    /// <inheritdoc />
    public bool HasManifest(uint depotId, ulong manifestId)
    {
        var path = GetCandidateManifestPath(depotId, manifestId);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        if (ValidateManifest(path))
            return true;

        _logger.LogWarning("Corrupt manifest file detected at {Path} for DepotId={DepotId}, ManifestId={ManifestId}. Safely discarding.", path, depotId, manifestId);
        try { File.Delete(path); } catch { }
        return false;
    }

    /// <inheritdoc />
    public string? GetManifestPath(uint depotId, ulong manifestId)
    {
        var path = GetCandidateManifestPath(depotId, manifestId);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        if (ValidateManifest(path))
            return path;

        _logger.LogWarning("Corrupt manifest file detected at {Path} for DepotId={DepotId}, ManifestId={ManifestId}. Safely discarding.", path, depotId, manifestId);
        try { File.Delete(path); } catch { }
        return null;
    }

    private string? GetCandidateManifestPath(uint depotId, ulong manifestId)
    {
        var depotDir = Path.Combine(_cacheRoot, depotId.ToString());
        var exactFile = Path.Combine(depotDir, $"{depotId}_{manifestId}.manifest");
        if (File.Exists(exactFile))
        {
            return exactFile;
        }

        // Flat lookup fallback in root
        var flatFile = Path.Combine(_cacheRoot, $"{depotId}_{manifestId}.manifest");
        return File.Exists(flatFile) ? flatFile : null;
    }

    /// <inheritdoc />
    public async Task<string> StoreManifestAsync(uint depotId, ulong manifestId, Stream sourceStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        var depotDir = Path.Combine(_cacheRoot, depotId.ToString());
        Directory.CreateDirectory(depotDir);

        var targetFile = Path.Combine(depotDir, $"{depotId}_{manifestId}.manifest");
        var tempFile = Path.Combine(depotDir, $"{depotId}_{manifestId}.tmp_{Guid.NewGuid():N}");

        try
        {
            await using (var dest = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await sourceStream.CopyToAsync(dest, ct).ConfigureAwait(false);
            }

            if (!ValidateManifest(tempFile))
            {
                File.Delete(tempFile);
                throw new InvalidDataException($"Stream provided for manifest {depotId}_{manifestId} does not appear to be a valid Steam manifest.");
            }

            File.Move(tempFile, targetFile, overwrite: true);
            _logger.LogDebug("Cached manifest {DepotId}_{ManifestId} to {Path}", depotId, manifestId, targetFile);
            return targetFile;
        }
        catch
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> StoreManifestFileAsync(uint depotId, ulong manifestId, string sourceFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            throw new FileNotFoundException("Source manifest file not found", sourceFilePath);

        if (!ValidateManifest(sourceFilePath))
            throw new InvalidDataException($"File at {sourceFilePath} is not a valid Steam manifest.");

        var depotDir = Path.Combine(_cacheRoot, depotId.ToString());
        Directory.CreateDirectory(depotDir);

        var targetFile = Path.Combine(depotDir, $"{depotId}_{manifestId}.manifest");
        if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
        {
            return targetFile;
        }

        await using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true))
        await using (var destStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        {
            await sourceStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }

        return targetFile;
    }

    /// <inheritdoc />
    public bool ValidateManifest(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            var info = new FileInfo(filePath);
            // Valid Steam manifests are at least 32 bytes
            if (info.Length < 32)
                return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[16];
            var read = fs.Read(header);
            if (read < 4) return false;

            // Reject cleartext error responses (HTML/JSON/404)
            byte b0 = header[0];
            if (b0 is (byte)'<' or (byte)'{' or (byte)'[' or (byte)'4' or (byte)'5' or (byte)'N' or (byte)'E')
            {
                return false;
            }

            uint magic32 = BitConverter.ToUInt32(header[..4]);
            ushort magic16 = BitConverter.ToUInt16(header[..2]);

            // 1. Standard Steam Protobuf Manifest: magic 0x71F617D0
            if (magic32 == SteamProtobufManifestMagic)
            {
                return fs.Length >= 8;
            }


            // 2. Gzip-compressed manifest: magic 0x8B1F
            if (magic16 == GzipMagic)
            {
                try
                {
                    fs.Position = 0;
                    using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    Span<byte> testBuffer = stackalloc byte[32];
                    var decompressed = gzip.Read(testBuffer);
                    return decompressed > 0;
                }
                catch
                {
                    return false;
                }
            }

            // 3. Raw Deflate-compressed ProtoManifest (used by DepotDownloader internal cache)
            try
            {
                fs.Position = 0;
                using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
                Span<byte> testBuffer = stackalloc byte[32];
                var decompressed = deflate.Read(testBuffer);
                return decompressed > 0;
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
