using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// A mod ready to install: an archive, a single mod file or an already extracted folder.
/// </summary>
public sealed record ModPackage
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required ModSourceRef Source { get; init; }
    public ModPackageLayout Layout { get; init; } = ModPackageLayout.Unknown;
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>PublishedFileId when the package is a Steam Workshop item.</summary>
    public ulong? WorkshopItemId { get; init; }
}

public sealed record ModInstallOptions
{
    /// <summary>Install even when the resolver is unsure (the user confirmed the plan).</summary>
    public bool AllowLowConfidence { get; init; }

    public static ModInstallOptions Default { get; } = new();
}

public enum ModInstallStatus
{
    Installed,
    NeedsConfirmation,
    Conflict,
    Failed
}

/// <summary>A file the new mod would overwrite that already belongs to another installed mod.</summary>
public sealed record ModFileConflict(string RelativePath, string OwnerModId);

public sealed record ModInstallResult
{
    public required ModInstallStatus Status { get; init; }
    public InstalledModRecord? Record { get; init; }
    public ModInstallPlan? Plan { get; init; }
    public IReadOnlyList<ModFileConflict> Conflicts { get; init; } = [];
    public string? Error { get; init; }

    public bool Succeeded => Status == ModInstallStatus.Installed;
}

/// <summary>
/// Shared install pipeline for every provider: stage, plan, check conflicts, copy with backups,
/// record in the ledger. Any failure rolls the game folder back to how it was.
/// </summary>
public interface IModInstallPipeline
{
    /// <summary>Stages the package and returns where its files would go, without changing the game folder.</summary>
    Task<ModInstallPlan> PlanAsync(GameInstance instance, ModPackage package, CancellationToken ct = default);

    /// <summary>Installs or updates a mod. Installing the same source again replaces the previous version.</summary>
    Task<ModInstallResult> InstallAsync(GameInstance instance, ModPackage package, ModInstallOptions? options = null, CancellationToken ct = default);

    /// <summary>Removes a mod's files, restores what it replaced and drops it from the ledger.</summary>
    Task<bool> UninstallAsync(GameInstance instance, string modId, CancellationToken ct = default);

    /// <summary>Moves a mod's files out of the game folder (disable) or back in (enable).</summary>
    Task<bool> SetEnabledAsync(GameInstance instance, string modId, bool enabled, CancellationToken ct = default);

    /// <summary>Gets the ledger of mods BlueStar installed in the instance.</summary>
    Task<ModLedger> GetLedgerAsync(GameInstance instance, CancellationToken ct = default);
}
