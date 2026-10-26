using System.IO.Compression;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.DepotBox;

/// <summary>
/// Copies the decryption keys a DepotBox lua lists (addappid(id, 1, "key")) into the depot key repository.
/// Besides the game's depots this includes the key of the depot that shares the game's AppID, which is the one
/// that decrypts Steam Workshop content.
/// </summary>
public sealed class DepotBoxKeyImporter
{
    private const string Source = "DepotBox lua";

    private readonly IDepotKeyRepository _keys;
    private readonly ILogger<DepotBoxKeyImporter> _logger;

    public DepotBoxKeyImporter(IDepotKeyRepository keys, ILogger<DepotBoxKeyImporter> logger)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Registers every key found in a parsed archive. Returns how many were registered.</summary>
    public async Task<int> ImportAsync(DepotBoxArchive archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var keys = archive.Games
            .Where(g => g.AppId != 0 && IsDepotKey(g.DepotKey))
            .GroupBy(g => g.AppId)
            .Select(g => new KeyValuePair<uint, string>(g.Key, g.First().DepotKey.Trim().ToLowerInvariant()))
            .ToList();

        if (keys.Count == 0) return 0;

        await _keys.RegisterKeysAsync(keys, Source, ct).ConfigureAwait(false);
        _logger.LogInformation("Registered {Count} depot keys from the DepotBox lua of AppId {AppId}", keys.Count, archive.AppId);
        return keys.Count;
    }

    /// <summary>
    /// Registers the keys of every DepotBox archive already in <paramref name="archivesDirectory"/>, so games imported
    /// before keys were collected can use the Workshop too. Unreadable archives are skipped.
    /// </summary>
    public async Task<int> ImportFolderAsync(string archivesDirectory, DepotBoxLuaParser parser, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (!Directory.Exists(archivesDirectory)) return 0;

        var total = 0;
        foreach (var zipPath in Directory.EnumerateFiles(archivesDirectory, "*.zip"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var luaEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                if (luaEntry == null) continue;

                using var reader = new StreamReader(luaEntry.Open());
                var lua = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                total += await ImportAsync(parser.Parse(lua), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipped DepotBox archive {Path} while collecting depot keys", zipPath);
            }
        }

        return total;
    }

    /// <summary>Depot keys are 32 bytes written as 64 hex characters.</summary>
    public static bool IsDepotKey(string? key) =>
        key is { Length: 64 } && key.All(Uri.IsHexDigit);
}
