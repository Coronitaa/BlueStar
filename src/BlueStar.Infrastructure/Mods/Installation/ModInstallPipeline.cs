using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Common;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods.Installation;

/// <summary>
/// Shared install pipeline for every mod provider. Game files a mod replaces are backed up under the
/// instance's data folder, disabled mods are parked there too, and every change is journaled so a
/// failure leaves the game folder untouched.
/// </summary>
public sealed class ModInstallPipeline : IModInstallPipeline
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz" };

    private readonly IModLedgerStore _ledgerStore;
    private readonly IModPlacementResolver _resolver;
    private readonly ILogger<ModInstallPipeline> _logger;
    private readonly string _stagingRoot;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _instanceLocks = new();

    public ModInstallPipeline(
        IModLedgerStore ledgerStore,
        IModPlacementResolver resolver,
        ILogger<ModInstallPipeline> logger,
        string? stagingRoot = null)
    {
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stagingRoot = stagingRoot ?? Path.Combine(Path.GetTempPath(), "BlueStar", "mod-staging");
    }

    /// <inheritdoc />
    public Task<ModLedger> GetLedgerAsync(GameInstance instance, CancellationToken ct = default) =>
        _ledgerStore.LoadAsync(instance.Id, ct);

    /// <inheritdoc />
    public async Task<ModInstallPlan> PlanAsync(GameInstance instance, ModPackage package, CancellationToken ct = default)
    {
        using var staged = await StageAsync(package, ct).ConfigureAwait(false);
        return _resolver.Resolve(instance, staged.Path, Hints(package));
    }

    /// <inheritdoc />
    public async Task<ModInstallResult> InstallAsync(
        GameInstance instance, ModPackage package, ModInstallOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(package);
        options ??= ModInstallOptions.Default;

        var gate = _instanceLocks.GetOrAdd(instance.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var staged = await StageAsync(package, ct).ConfigureAwait(false);
            var plan = _resolver.Resolve(instance, staged.Path, Hints(package));

            if (plan.Confidence == PlacementConfidence.Low && !options.AllowLowConfidence)
            {
                return new ModInstallResult { Status = ModInstallStatus.NeedsConfirmation, Plan = plan };
            }

            var ledger = await _ledgerStore.LoadAsync(instance.Id, ct).ConfigureAwait(false);
            var modId = package.Source.LedgerId;
            var previous = ledger.Find(modId);
            if (previous is { IsEnabled: false })
            {
                return Failed(plan, "Enable the mod before updating it.");
            }

            var owners = ledger.FileOwners(exceptModId: modId);
            var conflicts = plan.Operations
                .Where(op => owners.ContainsKey(op.RelativeDestination))
                .Select(op => new ModFileConflict(op.RelativeDestination, owners[op.RelativeDestination]))
                .ToList();
            if (conflicts.Count > 0)
            {
                return new ModInstallResult { Status = ModInstallStatus.Conflict, Plan = plan, Conflicts = conflicts };
            }

            var record = await ApplyAsync(instance, package, plan, ledger, previous, ct).ConfigureAwait(false);
            _logger.LogInformation("Installed mod {ModId} into {Instance} ({Count} files, {Strategy})",
                modId, instance.Name, record.Files.Count, plan.Strategy);
            return new ModInstallResult { Status = ModInstallStatus.Installed, Plan = plan, Record = record };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install mod {Name} into {Instance}", package.Name, instance.Name);
            return Failed(null, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UninstallAsync(GameInstance instance, string modId, CancellationToken ct = default)
    {
        var gate = _instanceLocks.GetOrAdd(instance.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ledger = await _ledgerStore.LoadAsync(instance.Id, ct).ConfigureAwait(false);
            var record = ledger.Find(modId);
            if (record == null) return false;

            using (var tx = BeginTransaction(instance, record.Id))
            {
                foreach (var file in record.Files)
                {
                    var normalPath = Path.Combine(instance.InstallPath, file.RelativePath);
                    var disabledPath = normalPath + ".disabled";
                    if (File.Exists(disabledPath))
                    {
                        try { File.Delete(disabledPath); } catch { }
                    }
                    if (record.IsEnabled)
                    {
                        tx.Remove(file.RelativePath, restoreBackup: file.ReplacedExisting);
                    }
                }

                await _ledgerStore.SaveAsync(instance.Id, ledger.WithoutMod(record.Id), ct).ConfigureAwait(false);
                tx.Commit();
            }

            ModFileTransaction.RemoveEmptyDirectories(instance.InstallPath, record.CreatedDirectories);
            DeleteQuietly(BackupDirectory(instance, record.Id));
            DeleteQuietly(DisabledDirectory(instance, record.Id));
            _logger.LogInformation("Uninstalled mod {ModId} from {Instance}", record.Id, instance.Name);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to uninstall mod {ModId} from {Instance}", modId, instance.Name);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetEnabledAsync(GameInstance instance, string modId, bool enabled, CancellationToken ct = default)
    {
        var gate = _instanceLocks.GetOrAdd(instance.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ledger = await _ledgerStore.LoadAsync(instance.Id, ct).ConfigureAwait(false);
            var record = ledger.Find(modId);
            if (record == null) return false;
            if (record.IsEnabled == enabled) return true;

            return enabled
                ? await EnableAsync(instance, ledger, record, ct).ConfigureAwait(false)
                : await DisableAsync(instance, ledger, record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to {Action} mod {ModId}", enabled ? "enable" : "disable", modId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── Install steps ──────────────────────────────────────────────────────

    private async Task<InstalledModRecord> ApplyAsync(
        GameInstance instance, ModPackage package, ModInstallPlan plan, ModLedger ledger,
        InstalledModRecord? previous, CancellationToken ct)
    {
        var modId = package.Source.LedgerId;
        var previousFiles = previous?.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, InstalledModFile>(StringComparer.OrdinalIgnoreCase);

        using var tx = BeginTransaction(instance, modId);
        var written = new List<InstalledModFile>();
        foreach (var op in plan.Operations)
        {
            ct.ThrowIfCancellationRequested();
            var ownedBefore = previousFiles.TryGetValue(op.RelativeDestination, out var old);
            written.Add(tx.Copy(op.SourcePath, op.RelativeDestination, ownedBefore, old?.ReplacedExisting ?? false));
        }

        // Files the previous version had but this one does not.
        var newPaths = written.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in previousFiles.Values.Where(f => !newPaths.Contains(f.RelativePath)))
        {
            tx.Remove(stale.RelativePath, restoreBackup: stale.ReplacedExisting);
        }

        var now = DateTimeOffset.UtcNow;
        var record = new InstalledModRecord
        {
            Id = modId,
            Name = package.Name,
            Source = package.Source,
            Strategy = plan.Strategy,
            InstalledAt = previous?.InstalledAt ?? now,
            UpdatedAt = now,
            Files = written,
            CreatedDirectories = (previous?.CreatedDirectories ?? [])
                .Concat(tx.CreatedDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Dependencies = package.Dependencies
        };

        await _ledgerStore.SaveAsync(instance.Id, ledger.WithMod(record), ct).ConfigureAwait(false);
        tx.Commit();
        return record;
    }

    private async Task<bool> DisableAsync(GameInstance instance, ModLedger ledger, InstalledModRecord record, CancellationToken ct)
    {
        try
        {
            foreach (var file in record.Files)
            {
                var source = Path.Combine(instance.InstallPath, file.RelativePath);
                if (File.Exists(source))
                {
                    var target = source + ".disabled";
                    File.Move(source, target, overwrite: true);
                }

                if (file.ReplacedExisting)
                {
                    var backup = Path.Combine(BackupDirectory(instance, record.Id), file.RelativePath);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                        File.Copy(backup, source, overwrite: true);
                    }
                }
            }

            await _ledgerStore.SaveAsync(instance.Id, ledger.WithMod(record with { IsEnabled = false }), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable mod {ModId}", record.Id);
            return false;
        }
    }

    private async Task<bool> EnableAsync(GameInstance instance, ModLedger ledger, InstalledModRecord record, CancellationToken ct)
    {
        var owners = ledger.FileOwners(exceptModId: record.Id);
        var clash = record.Files.FirstOrDefault(f => owners.ContainsKey(f.RelativePath));
        if (clash != null)
        {
            _logger.LogWarning("Cannot enable {ModId}: {Path} now belongs to {Owner}", record.Id, clash.RelativePath, owners[clash.RelativePath]);
            return false;
        }

        var parked = DisabledDirectory(instance, record.Id);
        try
        {
            foreach (var file in record.Files)
            {
                var activePath = Path.Combine(instance.InstallPath, file.RelativePath);
                var disabledPath = activePath + ".disabled";

                if (File.Exists(disabledPath))
                {
                    File.Move(disabledPath, activePath, overwrite: true);
                }
                else if (File.Exists(Path.Combine(parked, file.RelativePath)))
                {
                    var pSource = Path.Combine(parked, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
                    File.Copy(pSource, activePath, overwrite: true);
                }
            }

            await _ledgerStore.SaveAsync(instance.Id, ledger.WithMod(record with { IsEnabled = true }), ct).ConfigureAwait(false);
            DeleteQuietly(parked);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable mod {ModId}", record.Id);
            return false;
        }
    }

    // ── Staging ────────────────────────────────────────────────────────────

    private async Task<StagedPackage> StageAsync(ModPackage package, CancellationToken ct)
    {
        if (Directory.Exists(package.Path)) return new StagedPackage(package.Path, owned: false);
        if (!File.Exists(package.Path)) throw new FileNotFoundException("Mod package not found.", package.Path);

        var stagingDir = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var staged = new StagedPackage(stagingDir, owned: true);
        try
        {
            if (ArchiveExtensions.Contains(Path.GetExtension(package.Path)))
            {
                await Task.Run(() => ArchiveExtractor.ExtractToDirectory(package.Path, stagingDir), ct).ConfigureAwait(false);
            }
            else
            {
                File.Copy(package.Path, Path.Combine(stagingDir, Path.GetFileName(package.Path)));
            }

            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private sealed class StagedPackage : IDisposable
    {
        private readonly bool _owned;

        public StagedPackage(string path, bool owned)
        {
            Path = path;
            _owned = owned;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_owned) DeleteQuietly(Path);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private ModFileTransaction BeginTransaction(GameInstance instance, string modId) =>
        new(instance.InstallPath,
            BackupDirectory(instance, modId),
            Path.Combine(_ledgerStore.GetInstanceDataDirectory(instance.Id), "mod-journal", Guid.NewGuid().ToString("N")));

    private string BackupDirectory(GameInstance instance, string modId) =>
        Path.Combine(_ledgerStore.GetInstanceDataDirectory(instance.Id), "mod-backups", SafeId(modId));

    private string DisabledDirectory(GameInstance instance, string modId) =>
        Path.Combine(_ledgerStore.GetInstanceDataDirectory(instance.Id), "mods-disabled", SafeId(modId));

    private static string SafeId(string modId) => PathHelper.SanitizeFolderName(modId.Replace(':', '_'));

    private static ModPlacementHints Hints(ModPackage package) => new()
    {
        ModName = package.Name,
        Layout = package.Layout,
        WorkshopItemId = package.WorkshopItemId
    };

    private static ModInstallResult Failed(ModInstallPlan? plan, string error) =>
        new() { Status = ModInstallStatus.Failed, Plan = plan, Error = error };

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            // mods-disabled/ and mod-backups/ hold one folder per mod; drop the parent once the last one is gone.
            ModFileTransaction.DeleteIfEmpty(Path.GetDirectoryName(directory));
        }
        catch (IOException)
        {
            // Leftovers live outside the game folder and are retried on the next operation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
