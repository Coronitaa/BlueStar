using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.GZip;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;


namespace BlueStar.Infrastructure.Common;

/// <summary>
/// Universal archive extractor with support for ZIP, RAR (including RAR5), 7-Zip (7z), TAR, and GZIP.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>
    /// Safely extracts any supported archive to the destination directory.
    /// </summary>
    public static void ExtractToDirectory(string archivePath, string destinationDirectory, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive file was not found at '{archivePath}'.", archivePath);
        }

        Directory.CreateDirectory(destinationDirectory);
        var fullDestDir = Path.GetFullPath(destinationDirectory);

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        var readerOptions = new ReaderOptions();

        Exception? lastEx = null;

        // 1. Try format based on file extension or header checks
        try
        {
            if (ext == ".rar" || RarArchive.IsRarFile(archivePath))
            {
                using var archive = RarArchive.OpenArchive(archivePath, readerOptions);
                ExtractArchiveEntries(archive, fullDestDir, overwrite);
                return;
            }

            if (ext == ".7z" || SevenZipArchive.IsSevenZipFile(archivePath))
            {
                using var archive = SevenZipArchive.OpenArchive(archivePath, readerOptions);
                ExtractArchiveEntries(archive, fullDestDir, overwrite);
                return;
            }

            if (ext == ".tar" || TarArchive.IsTarFile(archivePath))
            {
                using var archive = TarArchive.OpenArchive(archivePath, readerOptions);
                ExtractArchiveEntries(archive, fullDestDir, overwrite);
                return;
            }

            if (ext == ".gz" || GZipArchive.IsGZipFile(archivePath))
            {
                using var archive = GZipArchive.OpenArchive(archivePath, readerOptions);
                ExtractArchiveEntries(archive, fullDestDir, overwrite);
                return;
            }

            if (ZipArchive.IsZipFile(archivePath, null))
            {
                using var archive = ZipArchive.OpenArchive(archivePath, readerOptions);
                ExtractArchiveEntries(archive, fullDestDir, overwrite);
                return;
            }
        }
        catch (Exception ex)
        {
            lastEx = ex;
        }

        // 2. Fallbacks in case the file extension does not match true format
        try
        {
            using var rarArchive = RarArchive.OpenArchive(archivePath, readerOptions);
            ExtractArchiveEntries(rarArchive, fullDestDir, overwrite);
            return;
        }
        catch { }

        try
        {
            using var szArchive = SevenZipArchive.OpenArchive(archivePath, readerOptions);
            ExtractArchiveEntries(szArchive, fullDestDir, overwrite);
            return;
        }
        catch { }

        try
        {
            using var zipArchive = ZipArchive.OpenArchive(archivePath, readerOptions);
            ExtractArchiveEntries(zipArchive, fullDestDir, overwrite);
            return;
        }
        catch { }

        // 3. Fallback to System.IO.Compression.ZipFile
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: overwrite);
            return;
        }
        catch
        {
            throw lastEx ?? new InvalidOperationException($"Unable to extract archive at '{archivePath}'. The format may be unsupported or corrupted.");
        }
    }

    private static void ExtractArchiveEntries(IArchive archive, string fullDestDir, bool overwrite)
    {
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            var entryKey = entry.Key ?? Path.GetFileName(fullDestDir);
            var destinationPath = Path.GetFullPath(Path.Combine(fullDestDir, entryKey));

            // Path traversal protection
            if (!destinationPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parentDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.WriteToFile(destinationPath, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = overwrite
            });
        }
    }
}



