using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Emulator provider for SmokeAPI / CreamAPI DLC and Steam API emulation.
/// </summary>
public sealed class SmokeApiEmulator : IEmulator
{
    private readonly IDlcInstaller _dlcInstaller;
    private readonly ILogger<SmokeApiEmulator> _logger;

    public string Id => "smokeapi";
    public string DisplayName => "SmokeAPI / CreamAPI";
    public string Description => "Steamworks API and DLC entitlement unlocker for Steam games.";

    public SmokeApiEmulator(IDlcInstaller dlcInstaller, ILogger<SmokeApiEmulator> logger)
    {
        _dlcInstaller = dlcInstaller ?? throw new ArgumentNullException(nameof(dlcInstaller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported(GameInstance instance)
    {
        // Available for all Steam instances or instances with DLCs
        return instance.AppId > 0 || (instance.Dlcs != null && instance.Dlcs.Count > 0);
    }

    public async Task<EmulatorStatus> GetStatusAsync(GameInstance instance, CancellationToken ct = default)
    {
        bool isInstalled = false;
        if (instance.Dlcs.Count > 0)
        {
            isInstalled = await _dlcInstaller.IsDlcInstalledAsync(instance, instance.Dlcs[0], ct).ConfigureAwait(false);
        }
        else
        {
            var testDlc = new DlcInfo { AppId = instance.AppId, Name = "Base", Depots = [] };
            isInstalled = await _dlcInstaller.IsDlcInstalledAsync(instance, testDlc, ct).ConfigureAwait(false);
        }

        return new EmulatorStatus
        {
            EmulatorId = Id,
            EmulatorName = DisplayName,
            IsConfigured = isInstalled,
            IsActive = isInstalled,
            StatusMessage = isInstalled ? "Active ● DLCs Unlocked" : "Inactive",
            BackendInfo = "SmokeAPI v4.x + cream_api.ini hook",
            NetworkInfo = "Steam Offline Emulation Mode",
            ConfigValues = new Dictionary<string, string>
            {
                ["DLCsCount"] = instance.Dlcs.Count.ToString(),
                ["TargetAppId"] = instance.AppId.ToString()
            }
        };
    }

    public async Task<bool> EnableAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (instance.Dlcs.Count == 0) return false;
        return await _dlcInstaller.InstallDlcAsync(instance, instance.Dlcs[0], ct).ConfigureAwait(false);
    }

    public async Task<bool> DisableAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (instance.Dlcs.Count == 0) return false;
        return await _dlcInstaller.UninstallDlcAsync(instance, instance.Dlcs[0], ct).ConfigureAwait(false);
    }

    public Task<bool> ConfigureAsync(GameInstance instance, IDictionary<string, string> config, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}
