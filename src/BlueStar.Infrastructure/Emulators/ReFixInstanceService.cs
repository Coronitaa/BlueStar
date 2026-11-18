using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Installs or removes a ReFix option on an instance and saves the result, UI-free. The WPF InstanceDetail view
/// runs the same steps inline (plus its fix-layer handling); the Avalonia head calls this.
/// </summary>
public sealed class ReFixInstanceService
{
    private readonly IEmulatorLifecycleService _lifecycle;
    private readonly IInstanceManager _instances;
    private readonly IReFixUpdateService? _updates;

    public ReFixInstanceService(IEmulatorLifecycleService lifecycle, IInstanceManager instances, IReFixUpdateService? updates = null)
    {
        _lifecycle = lifecycle;
        _instances = instances;
        _updates = updates;
    }

    /// <summary>
    /// Deploys <paramref name="optionId"/> and saves the instance. A version already set on <paramref name="instance"/>
    /// (the "update ReFix" flow sets the target version first) wins over the installed one.
    /// </summary>
    /// <returns>The saved instance, or null when the deploy failed (see the log).</returns>
    public async Task<GameInstance?> InstallAsync(GameInstance instance, string optionId, IProgress<DeployProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _lifecycle.DeployOrUpdateEmulatorWithDlcPreservationAsync(instance, optionId, progress, ct).ConfigureAwait(false))
            return null;

        var version = !string.IsNullOrWhiteSpace(instance.InstalledEmulatorVersion)
            ? instance.InstalledEmulatorVersion
            : _updates?.GetCurrentInstalledVersion() ?? ReFixEmulator.GetCurrentVersion(instance);
        var updated = instance with { EmulatorEnabled = true, EmulatorId = optionId, InstalledEmulatorVersion = version };
        updated = updated with { ExecutablePath = await DetectGameExeAsync(updated, ct).ConfigureAwait(false) ?? updated.ExecutablePath };
        return await _instances.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    // ReFix's detector knows where the real game exe is (Unreal: Binaries/Win64); off Windows it falls back to the configured exe.
    private static async Task<string?> DetectGameExeAsync(GameInstance instance, CancellationToken ct)
    {
        var targetDir = instance.InstallPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var deployPath = ReFixEmulator.GetReFixDeployPath(instance);
        if (string.IsNullOrEmpty(targetDir) || deployPath == null)
            return null;
        var (_, _, exe, _, _) = await ReFixEmulator.RunGameDetectorAsync(targetDir, Path.Combine(deployPath, "bin"), instance.ExecutablePath, ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
    }

    public async Task<GameInstance> UninstallAsync(GameInstance instance, IProgress<DeployProgress>? progress = null, CancellationToken ct = default)
    {
        await _lifecycle.UninstallEmulatorWithDlcPreservationAsync(instance, progress, ct).ConfigureAwait(false);
        var current = await _instances.GetByIdAsync(instance.Id, ct).ConfigureAwait(false) ?? instance;
        var updated = current with { EmulatorEnabled = false, EmulatorId = null, InstalledEmulatorVersion = null };
        return await _instances.UpdateAsync(updated, ct).ConfigureAwait(false);
    }
}
