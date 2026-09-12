using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Core.Storage;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Engine;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Instance;

/// <summary>
/// Manages game instances persisted as JSON files in a configurable root directory,
/// with support for zero-copy NTFS deployment from base depots, Copy-on-Write isolation,
/// and ReFix multi-instance configuration.
/// </summary>
public sealed class InstanceManager : IInstanceManager
{
    /// <inheritdoc />
    public event EventHandler? InstancesChanged;

    private readonly string _rootPath;
    private readonly string _depotsRootPath;
    private readonly ILogger<InstanceManager> _logger;
    private readonly ICommunityStatsService? _statsService;
    private readonly IInstanceStorageManager _storageManager;
    private readonly IEngineDetector _engineDetector;
    private readonly IReFixManager _refixManager;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceManager"/> class.
    /// </summary>
    public InstanceManager(
        ILogger<InstanceManager> logger,
        ICommunityStatsService? statsService = null,
        IInstanceStorageManager? storageManager = null,
        IEngineDetector? engineDetector = null,
        IReFixManager? refixManager = null,
        string? rootPath = null,
        string? depotsRootPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statsService = statsService;

        _rootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances");

        _depotsRootPath = depotsRootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "depots");

        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_depotsRootPath);

        var defaultLinker = new Win32Linker(NullLogger<Win32Linker>.Instance);
        _storageManager = storageManager ?? new InstanceStorageManager(defaultLinker, NullLogger<InstanceStorageManager>.Instance);
        _engineDetector = engineDetector ?? new EngineDetector(NullLogger<EngineDetector>.Instance);
        _refixManager = refixManager ?? new ReFixManager(NullLogger<ReFixManager>.Instance);
    }

    /// <summary>
    /// Resolves a unique name for a game instance given the existing instances.
    /// Appends (2), (3), etc. if names collide.
    /// </summary>
    public static string ResolveUniqueName(string baseName, IEnumerable<GameInstance>? existingInstances)
    {
        var existingNames = (existingInstances ?? []).Select(i => i.Name).ToList();
        return PathHelper.GenerateUniqueInstanceName(existingNames, baseName);
    }

    /// <summary>
    /// Resolves a non-colliding install path for a game instance given the existing instances.
    /// </summary>
    public static string ResolveNonCollidingInstallPath(string instanceName, string candidatePath, IEnumerable<GameInstance>? existingInstances)
    {
        var existingPaths = (existingInstances ?? []).Select(i => i.InstallPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var existingSet = new HashSet<string>(existingPaths.Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);

        var cleanCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!existingSet.Contains(cleanCandidate))
        {
            return cleanCandidate;
        }

        var parentDir = Path.GetDirectoryName(cleanCandidate) ?? string.Empty;
        var sanitized = PathHelper.SanitizeFolderName(instanceName);
        var targetPath = Path.Combine(parentDir, sanitized);
        if (!existingSet.Contains(targetPath))
        {
            return targetPath;
        }

        return PathHelper.GenerateUniqueInstallPath(parentDir, sanitized, existingPaths);
    }

    /// <inheritdoc />
    public string GetBaseDepotPath(uint appId)
    {
        return Path.Combine(_depotsRootPath, $"{appId}_base");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instances = new List<GameInstance>();

            if (!Directory.Exists(_rootPath))
                return instances.AsReadOnly();

            foreach (var dir in Directory.GetDirectories(_rootPath))
            {
                ct.ThrowIfCancellationRequested();

                var instanceFile = Path.Combine(dir, "instance.json");
                if (!File.Exists(instanceFile))
                    continue;

                try
                {
                    var json = await File.ReadAllTextAsync(instanceFile, ct).ConfigureAwait(false);
                    var instance = JsonSerializer.Deserialize<GameInstance>(json, JsonOptions);
                    if (instance is not null)
                        instances.Add(instance);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    _logger.LogWarning(ex, "Failed to read instance from {Path}", instanceFile);
                }
            }

            _logger.LogDebug("Loaded {Count} instances from {Root}", instances.Count, _rootPath);
            return instances.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceFile = Path.Combine(_rootPath, id.ToString(), "instance.json");
            if (!File.Exists(instanceFile))
                return null;

            var json = await File.ReadAllTextAsync(instanceFile, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GameInstance>(json, JsonOptions);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance> CreateAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var newInstance = instance with
            {
                Id = instance.Id == Guid.Empty ? Guid.NewGuid() : instance.Id,
                CreatedAt = now,
                UpdatedAt = now
            };

            var instanceDir = Path.Combine(_rootPath, newInstance.Id.ToString());
            Directory.CreateDirectory(instanceDir);

            var json = JsonSerializer.Serialize(newInstance, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"), json, ct).ConfigureAwait(false);

            _logger.LogInformation("Created instance {Id}: {Name} (AppId={AppId})",
                newInstance.Id, newInstance.Name, newInstance.AppId);

            _lock.Release();
            InstancesChanged?.Invoke(this, EventArgs.Empty);

            if (_statsService != null && newInstance.AppId > 0)
            {
                _ = _statsService.ReportInstanceAddedAsync(newInstance.AppId, newInstance.Name, CancellationToken.None);
            }

            return newInstance;
        }
        catch
        {
            _lock.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance> CreateInstanceFromDepotAsync(
        uint appId,
        string instanceName,
        string depotPath,
        string? customInstancePath = null,
        InstanceDeployOptions? deployOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instanceName);
        if (string.IsNullOrWhiteSpace(depotPath)) throw new ArgumentNullException(nameof(depotPath));

        var newId = Guid.NewGuid();
        var instanceContainerDir = Path.Combine(_rootPath, newId.ToString());
        Directory.CreateDirectory(instanceContainerDir);

        var instanceGamePath = !string.IsNullOrWhiteSpace(customInstancePath)
            ? customInstancePath
            : Path.Combine(instanceContainerDir, "game");

        // 1. Zero-copy storage deployment
        var deployResult = await _storageManager.CreateInstanceAsync(depotPath, instanceGamePath, deployOptions, ct).ConfigureAwait(false);
        if (!deployResult.Success)
        {
            throw new InvalidOperationException($"Zero-copy deployment failed: {deployResult.ErrorMessage}");
        }

        // 2. Engine detection
        var engine = await _engineDetector.DetectEngineAsync(instanceGamePath, ct).ConfigureAwait(false);
        var primaryExe = _engineDetector.FindPrimaryExecutable(instanceGamePath, instanceName);

        // 3. Isolated ReFix / Goldberg emulator configuration
        int existingCount = Directory.GetDirectories(_rootPath).Length;
        var steamId = _refixManager.GenerateUniqueSteamId(newId, slotIndex: existingCount);
        var listenPort = _refixManager.GenerateUniquePort(slotIndex: existingCount);

        var refixConfig = new InstanceReFixConfig
        {
            AppId = appId,
            SteamId = steamId,
            AccountName = string.IsNullOrWhiteSpace(instanceName) ? $"Player_{existingCount}" : instanceName,
            ListenPort = listenPort,
            DisableOverlay = true,
            LocalSave = true
        };

        await _refixManager.ConfigureInstanceSettingsAsync(instanceGamePath, refixConfig, ct).ConfigureAwait(false);

        // 4. Create and persist GameInstance record
        var now = DateTimeOffset.UtcNow;
        var gameInstance = new GameInstance
        {
            Id = newId,
            Name = instanceName,
            AppId = appId,
            InstallPath = instanceGamePath,
            ExecutablePath = primaryExe,
            Status = InstanceStatus.Ready,
            Engine = engine,
            EmulatorEnabled = true,
            EmulatorId = "refix",
            CreatedAt = now,
            UpdatedAt = now
        };

        var json = JsonSerializer.Serialize(gameInstance, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(instanceContainerDir, "instance.json"), json, ct).ConfigureAwait(false);

        _logger.LogInformation("Successfully deployed zero-copy game instance {Id} ({Name}) from depot {Depot}",
            newId, instanceName, depotPath);

        InstancesChanged?.Invoke(this, EventArgs.Empty);
        return gameInstance;
    }

    /// <inheritdoc />
    public async Task<GameInstance> CloneInstanceAsync(
        Guid sourceInstanceId,
        string newInstanceName,
        CancellationToken ct = default)
    {
        var sourceInstance = await GetByIdAsync(sourceInstanceId, ct).ConfigureAwait(false);
        if (sourceInstance == null)
            throw new InvalidOperationException($"Source instance {sourceInstanceId} not found.");

        var newId = Guid.NewGuid();
        var instanceContainerDir = Path.Combine(_rootPath, newId.ToString());
        Directory.CreateDirectory(instanceContainerDir);

        var newGamePath = Path.Combine(instanceContainerDir, "game");

        // Zero-copy clone from source instance
        var deployResult = await _storageManager.CreateInstanceAsync(sourceInstance.InstallPath, newGamePath, null, ct).ConfigureAwait(false);
        if (!deployResult.Success)
        {
            throw new InvalidOperationException($"Failed to clone instance: {deployResult.ErrorMessage}");
        }

        // Configure unique emulator settings only if source instance has an emulator
        bool sourceHasEmulator = sourceInstance.EmulatorEnabled ||
            !string.IsNullOrWhiteSpace(sourceInstance.EmulatorId) ||
            ReFixEmulator.IsEmulatorInstalled(sourceInstance.InstallPath);

        if (sourceHasEmulator)
        {
            int existingCount = Directory.GetDirectories(_rootPath).Length;
            var steamId = _refixManager.GenerateUniqueSteamId(newId, slotIndex: existingCount);
            var listenPort = _refixManager.GenerateUniquePort(slotIndex: existingCount);

            var refixConfig = new InstanceReFixConfig
            {
                AppId = sourceInstance.AppId,
                SteamId = steamId,
                AccountName = newInstanceName,
                ListenPort = listenPort,
                DisableOverlay = true,
                LocalSave = true
            };

            await _refixManager.ConfigureInstanceSettingsAsync(newGamePath, refixConfig, ct).ConfigureAwait(false);
        }


        string? newExePath = null;
        if (!string.IsNullOrWhiteSpace(sourceInstance.ExecutablePath))
        {
            try
            {
                var relExe = Path.GetRelativePath(sourceInstance.InstallPath, sourceInstance.ExecutablePath);
                var candidateExe = Path.Combine(newGamePath, relExe);
                if (File.Exists(candidateExe))
                {
                    newExePath = candidateExe;
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(newExePath))
        {
            newExePath = _engineDetector.FindPrimaryExecutable(newGamePath, newInstanceName);
        }

        var engine = await _engineDetector.DetectEngineAsync(newGamePath, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var clonedInstance = sourceInstance with
        {
            Id = newId,
            Name = newInstanceName,
            InstallPath = newGamePath,
            ExecutablePath = newExePath ?? sourceInstance.ExecutablePath,
            Engine = (engine != null && engine.Type != EngineType.Generic) ? engine : sourceInstance.Engine,
            CreatedAt = now,
            UpdatedAt = now
        };

        var json = JsonSerializer.Serialize(clonedInstance, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(instanceContainerDir, "instance.json"), json, ct).ConfigureAwait(false);

        _logger.LogInformation("Cloned instance {SourceId} into {NewId} ({Name})", sourceInstanceId, newId, newInstanceName);

        InstancesChanged?.Invoke(this, EventArgs.Empty);
        return clonedInstance;
    }

    /// <inheritdoc />
    public async Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceDir = Path.Combine(_rootPath, instance.Id.ToString());
            if (!Directory.Exists(instanceDir))
                throw new InvalidOperationException($"Instance {instance.Id} not found.");

            var updatedInstance = instance.UpdatedAt > DateTimeOffset.MinValue 
                ? instance 
                : instance with { UpdatedAt = DateTimeOffset.UtcNow };
            var json = JsonSerializer.Serialize(updatedInstance, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"), json, ct).ConfigureAwait(false);

            _logger.LogInformation("Updated instance {Id}: {Name}", instance.Id, instance.Name);

            _lock.Release();
            InstancesChanged?.Invoke(this, EventArgs.Empty);
            return updatedInstance;
        }
        catch
        {
            _lock.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceDir = Path.Combine(_rootPath, id.ToString());
            if (!Directory.Exists(instanceDir))
            {
                _lock.Release();
                return false;
            }

            var instanceFile = Path.Combine(instanceDir, "instance.json");
            if (File.Exists(instanceFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(instanceFile, ct).ConfigureAwait(false);
                    var instance = JsonSerializer.Deserialize<GameInstance>(json, JsonOptions);
                    if (instance != null)
                    {
                        // Clean up shortcuts from Desktop and Start Menu
                        ShortcutHelper.RemoveGameShortcuts(instance.Name, instance.ExecutablePath, instance.InstallPath);

                        if (!string.IsNullOrWhiteSpace(instance.InstallPath))
                        {
                            // Clean up hardlinks & junctions safely
                            await _storageManager.DeleteInstanceAsync(instance.InstallPath, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
            }

            if (Directory.Exists(instanceDir))
            {
                Directory.Delete(instanceDir, recursive: true);
            }

            _logger.LogInformation("Deleted instance {Id}", id);

            _lock.Release();
            InstancesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _lock.Release();
            throw;
        }
    }
}
