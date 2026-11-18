using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Linux SteamCMD: Valve's tarball over HTTPS, checked against a pinned SHA-256 (it has not changed since
/// 2018-01-05). There is no Authenticode on Linux; after the first run steamcmd.sh updates itself from Valve.
/// It needs 32-bit glibc (lib32gcc-s1 / glibc.i686), which the requirements page checks.
/// </summary>
public sealed partial class SteamCmdWorkshopClient
{
    public const string SteamCmdLinuxUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    public const string SteamCmdLinuxSha256 = "cebf0046bfd08cf45da6bc094ae47aa39ebf4155e5ede41373b579b8f1071e7c";

    internal static bool HasExpectedHash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).Equals(SteamCmdLinuxSha256, StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Ok, string? Error)> EnsureLinuxSteamCmdAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (File.Exists(SteamCmdExe))
            return (true, null);

        _logger.LogInformation("Downloading SteamCMD for Linux from Valve into {Dir}", _steamCmdDir);
        progress?.Report(8.0);
        try
        {
            var data = await _http.GetByteArrayAsync(SteamCmdLinuxUrl, ct).ConfigureAwait(false);
            if (!HasExpectedHash(data))
            {
                _logger.LogWarning("SteamCMD tarball hash mismatch: refusing to extract it");
                return (false, "The downloaded SteamCMD does not match Valve's known file and was not installed.");
            }

            Directory.CreateDirectory(_steamCmdDir);
            await using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, _steamCmdDir, overwriteFiles: true, ct).ConfigureAwait(false); // keeps the exec bits
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Could not download SteamCMD for Linux");
            return (false, "SteamCMD could not be downloaded from Valve.");
        }
    }
}
