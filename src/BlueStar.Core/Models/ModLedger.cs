using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

/// <summary>
/// Where a mod came from. <see cref="Provider"/> is a short id such as "workshop", "thunderstore",
/// "curseforge", "nexus", "modio" or "local"; <see cref="RemoteId"/> is the provider's own id.
/// </summary>
public sealed record ModSourceRef
{
    public const string LocalProvider = "local";

    public required string Provider { get; init; }
    public required string RemoteId { get; init; }
    public string? Version { get; init; }
    public string? FileId { get; init; }
    public string? PageUrl { get; init; }
    public string? IconUrl { get; init; }

    /// <summary>Stable ledger id for this source, e.g. "thunderstore:denikson-BepInExPack_Valheim".</summary>
    [JsonIgnore]
    public string LedgerId => $"{Provider}:{RemoteId}".ToLowerInvariant();

    /// <summary>Source for a file the user picked from disk.</summary>
    public static ModSourceRef Local(string name) => new()
    {
        Provider = LocalProvider,
        RemoteId = name
    };
}

/// <summary>
/// One file a mod wrote. <see cref="RelativePath"/> is relative to the instance's install folder.
/// </summary>
public sealed record InstalledModFile(string RelativePath, long SizeBytes, string Sha256)
{
    /// <summary>True when installing this file replaced a file that already existed (backed up first).</summary>
    public bool ReplacedExisting { get; init; }
}

/// <summary>
/// Everything BlueStar needs to update, disable or uninstall a mod cleanly.
/// </summary>
public sealed record InstalledModRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ModSourceRef Source { get; init; }
    public required string Strategy { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<InstalledModFile> Files { get; init; } = [];

    /// <summary>Folders the install created, so uninstall can remove them again if they end up empty.</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];

    /// <summary>Ledger ids of mods this one needs.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    [JsonIgnore]
    public string? Version => Source.Version;

    [JsonIgnore]
    public long TotalSizeBytes => Files.Sum(f => f.SizeBytes);
}

/// <summary>
/// Per-instance record of installed mods. Immutable: every change returns a new ledger.
/// </summary>
public sealed record ModLedger
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<InstalledModRecord> Mods { get; init; } = [];

    public static ModLedger Empty { get; } = new();

    public InstalledModRecord? Find(string modId) =>
        Mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a ledger with <paramref name="record"/> added, or replacing the mod with the same id.</summary>
    public ModLedger WithMod(InstalledModRecord record) => this with
    {
        Mods = Mods
            .Where(m => !string.Equals(m.Id, record.Id, StringComparison.OrdinalIgnoreCase))
            .Append(record)
            .ToList()
    };

    public ModLedger WithoutMod(string modId) => this with
    {
        Mods = Mods
            .Where(m => !string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            .ToList()
    };

    /// <summary>Maps each file path owned by an enabled mod to its mod id, skipping <paramref name="exceptModId"/>.</summary>
    public IReadOnlyDictionary<string, string> FileOwners(string? exceptModId = null)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods)
        {
            if (!mod.IsEnabled || string.Equals(mod.Id, exceptModId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in mod.Files)
            {
                owners[file.RelativePath] = mod.Id;
            }
        }

        return owners;
    }
}
