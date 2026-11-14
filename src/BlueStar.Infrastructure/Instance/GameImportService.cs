using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Steam;

namespace BlueStar.Infrastructure.Instance;

/// <summary>
/// Adds an installed game to the library, from any folder or from a local Steam library. UI-free, so every head
/// (WPF, Avalonia) imports the same way: engine detection, main exe, unique name, optional Steam metadata.
/// </summary>
public sealed class GameImportService
{
    private readonly IInstanceManager _instances;
    private readonly IEngineDetector _engines;
    private readonly IMetadataProvider? _metadata;

    public GameImportService(IInstanceManager instances, IEngineDetector engines, IMetadataProvider? metadata = null)
    {
        _instances = instances;
        _engines = engines;
        _metadata = metadata;
    }

    public Task<GameInstance> ImportFolderAsync(string folder, string? name = null, uint appId = 0, CancellationToken ct = default)
    {
        var fallbackName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ImportAsync(folder, string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim(), appId, InstanceOrigin.ImportedFolder, ct);
    }

    public Task<GameInstance> ImportSteamGameAsync(InstalledSteamGame game, CancellationToken ct = default) =>
        ImportAsync(game.FullPath, game.Name, game.AppId, InstanceOrigin.Steam, ct);

    private async Task<GameInstance> ImportAsync(string folder, string name, uint appId, InstanceOrigin origin, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var existing = await _instances.GetAllAsync(ct).ConfigureAwait(false);
        var trimmed = folder.TrimEnd('\\', '/');
        var colliding = existing.FirstOrDefault(i => string.Equals(i.InstallPath?.TrimEnd('\\', '/'), trimmed, PathComparison));
        if (colliding != null)
            throw new InvalidOperationException($"This folder is already in the library as \"{colliding.Name}\".");

        var engine = await _engines.DetectEngineAsync(folder, ct).ConfigureAwait(false);
        var exe = _engines.FindPrimaryExecutable(folder, name);
        var (meta, dlcs) = await TryGetMetadataAsync(appId, ct).ConfigureAwait(false);

        var instance = new GameInstance
        {
            Name = PathHelper.GenerateUniqueInstanceName(existing.Select(i => i.Name).ToList(), name),
            AppId = appId,
            InstallPath = folder,
            ExecutablePath = exe,
            Engine = engine,
            Metadata = meta,
            Dlcs = dlcs,
            Origin = origin,
            Status = File.Exists(exe) ? InstanceStatus.Ready : InstanceStatus.NotInstalled,
        };
        return await _instances.CreateAsync(instance, ct).ConfigureAwait(false);
    }

    // Spacewar (480) is the ReFix placeholder id, not a real game.
    private async Task<(GameMetadata?, IReadOnlyList<DlcInfo>)> TryGetMetadataAsync(uint appId, CancellationToken ct)
    {
        if (_metadata == null || appId == 0 || appId == 480)
            return (null, []);
        try
        {
            return (await _metadata.GetMetadataAsync(appId, ct).ConfigureAwait(false),
                    await _metadata.GetDlcListAsync(appId, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, []); // offline: the game still imports, just without store art and DLC list
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
