using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Manages the acquisition, persistent storage, and resolution of 64-character
/// hexadecimal Steam depot decryption keys from local cache, community key stores, and providers.
/// </summary>
public interface IDepotKeyRepository
{
    /// <summary>
    /// Attempts to retrieve the decryption key for a specific depot.
    /// </summary>
    Task<string?> GetKeyAsync(uint depotId, CancellationToken ct = default);

    /// <summary>
    /// Resolves keys for multiple depots in a single operation.
    /// </summary>
    Task<IReadOnlyDictionary<uint, string>> GetKeysAsync(IEnumerable<uint> depotIds, CancellationToken ct = default);

    /// <summary>
    /// Registers or updates a known decryption key for a depot.
    /// </summary>
    Task RegisterKeyAsync(uint depotId, string hexKey, string source = "Manual", CancellationToken ct = default);

    /// <summary>
    /// Batch-registers multiple keys and persists them to the local cache.
    /// </summary>
    Task RegisterKeysAsync(IEnumerable<KeyValuePair<uint, string>> keys, string source = "Batch", CancellationToken ct = default);

    /// <summary>
    /// Parses and imports depot keys from community JSON format (e.g. {"depotId": "hexKey"} or {"depotId": {"key": "..."}}).
    /// </summary>
    Task ImportFromJsonAsync(string jsonContent, CancellationToken ct = default);

    /// <summary>
    /// Parses and imports depot keys from Lua format (e.g. addappid(depotId, 0, "hexKey")).
    /// </summary>
    Task ImportFromLuaAsync(string luaContent, CancellationToken ct = default);
}
