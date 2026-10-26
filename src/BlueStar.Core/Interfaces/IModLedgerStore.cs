using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Persists the per-instance ledger of installed mods.
/// </summary>
public interface IModLedgerStore
{
    /// <summary>Loads the ledger for an instance, or an empty ledger when none exists yet.</summary>
    Task<ModLedger> LoadAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>Saves the ledger atomically, so a crash never leaves a half-written file.</summary>
    Task SaveAsync(Guid instanceId, ModLedger ledger, CancellationToken ct = default);

    /// <summary>Folder that holds an instance's ledger, mod backups and disabled mods.</summary>
    string GetInstanceDataDirectory(Guid instanceId);
}
