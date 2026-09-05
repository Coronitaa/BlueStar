using System;
using System.IO;
using System.IO.Compression;

namespace BlueStar.Infrastructure.Downloader;

/// <summary>
/// Extracts the authentic creation/release timestamp from Steam .manifest binary files.
/// </summary>
public static class SteamManifestDateHelper
{
    /// <summary>
    /// Reads the internal creation timestamp of a Steam .manifest file.
    /// </summary>
    /// <param name="manifestFilePath">Absolute path to the .manifest file.</param>
    /// <returns>The creation timestamp if successfully parsed, or null.</returns>
    public static DateTimeOffset? GetManifestCreationDate(string manifestFilePath)
    {
        if (string.IsNullOrWhiteSpace(manifestFilePath) || !File.Exists(manifestFilePath))
            return null;

        // 1. Try official SteamKit2 DepotManifest parser first
        try
        {
            var manifest = SteamKit2.DepotManifest.LoadFromFile(manifestFilePath);
            if (manifest != null && manifest.CreationTime.Year >= 2005 && manifest.CreationTime <= DateTime.UtcNow.AddDays(2))
            {
                return new DateTimeOffset(manifest.CreationTime);
            }
        }
        catch { }

        try
        {
            using var fs = new FileStream(manifestFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return null;

            using var reader = new BinaryReader(fs);
            uint magic = reader.ReadUInt32();
            ushort magic16 = (ushort)(magic & 0xFFFF);

            // 2. Steam Protobuf Manifest Magic: 0x71F617D0
            if (magic == 0x71F617D0)
            {
                try
                {
                    // Byte 4..7: payload length (uint32)
                    // Byte 8+: Deflate compressed payload stream
                    fs.Position = 8;
                    using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    using var ms = new MemoryStream();
                    deflate.CopyTo(ms);
                    var payload = ms.ToArray();

                    var date = ExtractProtobufCreationTime(payload);
                    if (date.HasValue)
                        return date;
                }
                catch { }
            }

            // 3. Gzip-compressed manifest: magic 0x8B1F
            if (magic16 == 0x8B1F)
            {
                try
                {
                    fs.Position = 0;
                    using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    using var ms = new MemoryStream();
                    gzip.CopyTo(ms);
                    var payload = ms.ToArray();

                    var date = ExtractProtobufCreationTime(payload);
                    if (date.HasValue)
                        return date;
                }
                catch { }
            }

            // 4. Raw Deflate payload without header
            try
            {
                fs.Position = 0;
                using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
                using var ms = new MemoryStream();
                deflate.CopyTo(ms);
                var payload = ms.ToArray();

                var date = ExtractProtobufCreationTime(payload);
                if (date.HasValue)
                    return date;
            }
            catch { }

            // 5. Fallback: inspect raw bytes for valid Protobuf varint tag 3 (0x18) or tag 4 (0x20)
            fs.Position = 0;
            var buffer = new byte[Math.Min(fs.Length, 8192)];
            int read = fs.Read(buffer, 0, buffer.Length);
            var rawDate = ExtractProtobufCreationTime(buffer[..read]);
            if (rawDate.HasValue)
                return rawDate;

            // 6. Fallback: check file last write time if within reasonable range
            var lastWrite = File.GetLastWriteTimeUtc(manifestFilePath);
            if (lastWrite.Year >= 2005 && lastWrite <= DateTime.UtcNow)
            {
                return new DateTimeOffset(lastWrite);
            }
        }
        catch { }

        return null;
    }

    private static DateTimeOffset? ExtractProtobufCreationTime(byte[] payload)
    {
        if (payload == null || payload.Length < 5) return null;

        // Tag 3 wire type 0 (varint): (3 << 3) | 0 = 24 = 0x18 (Steam Protobuf creation_time)
        // Tag 4 wire type 0 (varint): (4 << 3) | 0 = 32 = 0x20
        for (int i = 0; i < payload.Length - 5; i++)
        {
            byte tag = payload[i];
            if (tag == 0x18 || tag == 0x20)
            {
                long value = 0;
                int shift = 0;
                int j = i + 1;
                while (j < payload.Length && shift < 64)
                {
                    byte b = payload[j++];
                    value |= (long)(b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) break;
                }

                // Check if value is a valid unix timestamp (between Jan 2008 and Dec 2035)
                // 1200000000 = 2008-01-10
                // 2080000000 = 2035-12-01
                if (value >= 1200000000 && value <= 2080000000)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(value);
                }
            }
        }

        return null;
    }
}
