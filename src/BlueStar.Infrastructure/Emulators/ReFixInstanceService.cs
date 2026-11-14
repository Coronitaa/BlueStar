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

    /// <returns>The saved instance, or null when the deploy failed (see the log).</returns>
    public async Task<GameInstance?> InstallAsync(GameInstance instance, string optionId, IProgress<DeployProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _lifecycle.DeployOrUpdateEmulatorWithDlcPreservationAsync(instance, optionId, progress, ct).ConfigureAwait(false))
            return null;

        var current = await _instances.GetByIdAsync(instance.Id, ct).ConfigureAwait(false) ?? instance;
        var version = !string.IsNullOrWhiteSpace(current.InstalledEmulatorVersion)
            ? current.InstalledEmulatorVersion
            : _updates?.GetCurrentInstalledVersion() ?? ReFixEmulator.GetCurrentVersion(current);
        var updated = current with { EmulatorEnabled = true, EmulatorId = optionId, InstalledEmulatorVersion = version };
        return await _instances.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task<GameInstance> UninstallAsync(GameInstance instance, IProgress<DeployProgress>? progress = null, CancellationToken ct = default)
    {
        await _lifecycle.UninstallEmulatorWithDlcPreservationAsync(instance, progress, ct).ConfigureAwait(false);
        var current = await _instances.GetByIdAsync(instance.Id, ct).ConfigureAwait(false) ?? instance;
        var updated = current with { EmulatorEnabled = false, EmulatorId = null, InstalledEmulatorVersion = null };
        return await _instances.UpdateAsync(updated, ct).ConfigureAwait(false);
    }
}
