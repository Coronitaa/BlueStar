using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Service that generates and manages isolated per-instance ReFix / Goldberg emulator configurations.
/// </summary>
public sealed class ReFixManager : IReFixManager
{
    private readonly ILogger<ReFixManager> _logger;

    public ReFixManager(ILogger<ReFixManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> ConfigureInstanceSettingsAsync(
        string instancePath,
        InstanceReFixConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            _logger.LogError("Cannot configure emulator settings: instance directory '{Path}' not found", instancePath);
            return false;
        }

        try
        {
            // Discover all steam_settings locations or use root
            var settingsDirs = Directory.GetDirectories(instancePath, "steam_settings", SearchOption.AllDirectories);
            if (settingsDirs.Length == 0)
            {
                var rootSettings = Path.Combine(instancePath, "steam_settings");
                Directory.CreateDirectory(rootSettings);
                settingsDirs = [rootSettings];
            }

            foreach (var dir in settingsDirs)
            {
                Directory.CreateDirectory(dir);

                // 1. AppID
                await File.WriteAllTextAsync(Path.Combine(dir, "steam_appid.txt"), config.AppId.ToString(), ct).ConfigureAwait(false);

                // 2. SteamID (64-bit)
                await File.WriteAllTextAsync(Path.Combine(dir, "force_steamid.txt"), config.SteamId.ToString(), ct).ConfigureAwait(false);

                // 3. Player Account Name
                await File.WriteAllTextAsync(Path.Combine(dir, "force_account_name.txt"), config.AccountName, ct).ConfigureAwait(false);

                // 4. LAN Listen Port
                await File.WriteAllTextAsync(Path.Combine(dir, "listen_port.txt"), config.ListenPort.ToString(), ct).ConfigureAwait(false);

                // 5. Disable Overlay
                if (config.DisableOverlay)
                {
                    await File.WriteAllTextAsync(Path.Combine(dir, "disable_overlay.txt"), "1", ct).ConfigureAwait(false);
                }
                else
                {
                    var file = Path.Combine(dir, "disable_overlay.txt");
                    if (File.Exists(file)) File.Delete(file);
                }

                // 6. Local Save
                if (config.LocalSave)
                {
                    await File.WriteAllTextAsync(Path.Combine(dir, "local_save.txt"), "1", ct).ConfigureAwait(false);
                }
                else
                {
                    var file = Path.Combine(dir, "local_save.txt");
                    if (File.Exists(file)) File.Delete(file);
                }

                // 7. Disable Networking
                if (config.DisableNetworking)
                {
                    await File.WriteAllTextAsync(Path.Combine(dir, "disable_networking.txt"), "1", ct).ConfigureAwait(false);
                }
                else
                {
                    var file = Path.Combine(dir, "disable_networking.txt");
                    if (File.Exists(file)) File.Delete(file);
                }
            }

            // Ensure isolated saves directory
            Directory.CreateDirectory(Path.Combine(instancePath, "saves"));

            _logger.LogInformation(
                "Configured isolated ReFix settings for instance {Path}: SteamID={SteamId}, User={User}, Port={Port}",
                instancePath, config.SteamId, config.AccountName, config.ListenPort);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure ReFix settings for instance {Path}", instancePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<InstanceReFixConfig?> ReadInstanceSettingsAsync(string instancePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            return null;

        try
        {
            var settingsDirs = Directory.GetDirectories(instancePath, "steam_settings", SearchOption.AllDirectories);
            var targetDir = settingsDirs.FirstOrDefault() ?? Path.Combine(instancePath, "steam_settings");

            if (!Directory.Exists(targetDir)) return null;

            uint appId = 0;
            var appIdFile = Path.Combine(targetDir, "steam_appid.txt");
            if (File.Exists(appIdFile))
            {
                var text = (await File.ReadAllTextAsync(appIdFile, ct).ConfigureAwait(false)).Trim();
                uint.TryParse(text, out appId);
            }

            ulong steamId = 76561198000000001UL;
            var steamIdFile = Path.Combine(targetDir, "force_steamid.txt");
            if (File.Exists(steamIdFile))
            {
                var text = (await File.ReadAllTextAsync(steamIdFile, ct).ConfigureAwait(false)).Trim();
                ulong.TryParse(text, out steamId);
            }

            string accountName = "Player_1";
            var userFile = Path.Combine(targetDir, "force_account_name.txt");
            if (File.Exists(userFile))
            {
                accountName = (await File.ReadAllTextAsync(userFile, ct).ConfigureAwait(false)).Trim();
            }

            int listenPort = 47584;
            var portFile = Path.Combine(targetDir, "listen_port.txt");
            if (File.Exists(portFile))
            {
                var text = (await File.ReadAllTextAsync(portFile, ct).ConfigureAwait(false)).Trim();
                int.TryParse(text, out listenPort);
            }

            bool disableOverlay = File.Exists(Path.Combine(targetDir, "disable_overlay.txt"));
            bool localSave = File.Exists(Path.Combine(targetDir, "local_save.txt"));
            bool disableNetworking = File.Exists(Path.Combine(targetDir, "disable_networking.txt"));

            return new InstanceReFixConfig
            {
                AppId = appId,
                SteamId = steamId,
                AccountName = string.IsNullOrWhiteSpace(accountName) ? "Player_1" : accountName,
                ListenPort = listenPort > 0 ? listenPort : 47584,
                DisableOverlay = disableOverlay,
                LocalSave = localSave,
                DisableNetworking = disableNetworking
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ReFix settings from {Path}", instancePath);
            return null;
        }
    }

    /// <inheritdoc />
    public ulong GenerateUniqueSteamId(Guid instanceId, int slotIndex = 0)
    {
        const ulong BaseSteamId = 76561198000000000UL;

        if (instanceId == Guid.Empty)
        {
            return BaseSteamId + 1UL + (ulong)slotIndex;
        }

        // Deterministic hash based on Instance Guid
        byte[] bytes = instanceId.ToByteArray();
        uint hash = BitConverter.ToUInt32(bytes, 0) ^ BitConverter.ToUInt32(bytes, 4) ^ BitConverter.ToUInt32(bytes, 8);
        ulong offset = (hash % 800000000UL) + (ulong)(slotIndex * 1000) + 1UL;

        return BaseSteamId + offset;
    }

    /// <inheritdoc />
    public int GenerateUniquePort(int slotIndex = 0)
    {
        const int BasePort = 47584;
        return BasePort + (slotIndex % 1000);
    }
}
