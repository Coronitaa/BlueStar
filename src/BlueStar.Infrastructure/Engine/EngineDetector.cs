using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Engine;

/// <summary>
/// Infallible game engine detector that inspects directory structures, signature files,
/// PE binary metadata, embedded magic headers, and file systems.
/// </summary>
public sealed class EngineDetector : IEngineDetector
{
    private readonly ILogger<EngineDetector> _logger;

    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        MaxRecursionDepth = 4,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    public EngineDetector(ILogger<EngineDetector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<EngineInfo> DetectEngineAsync(string installPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return Task.FromResult(CreateGenericEngine());
        }

        return Task.Run(() => DetectEngineInternal(installPath), ct);
    }

    private EngineInfo DetectEngineInternal(string installPath)
    {
        try
        {
            // ── 1. UNREAL ENGINE ──
            var unrealInfo = CheckUnrealEngine(installPath);
            if (unrealInfo != null)
            {
                _logger.LogInformation("Detected Unreal Engine ({Version}) at {Path}", unrealInfo.Version ?? "Unknown", installPath);
                return unrealInfo;
            }

            // ── 2. UNITY ENGINE ──
            var unityInfo = CheckUnityEngine(installPath);
            if (unityInfo != null)
            {
                _logger.LogInformation("Detected Unity Engine ({Version}) at {Path}", unityInfo.Version ?? "Unknown", installPath);
                return unityInfo;
            }

            // ── 3. GODOT ENGINE ──
            var godotInfo = CheckGodotEngine(installPath);
            if (godotInfo != null)
            {
                _logger.LogInformation("Detected Godot Engine at {Path}", installPath);
                return godotInfo;
            }

            // ── 4. SOURCE & SOURCE 2 ENGINE ──
            var sourceInfo = CheckSourceEngine(installPath);
            if (sourceInfo != null)
            {
                _logger.LogInformation("Detected {Engine} at {Path}", sourceInfo.Name, installPath);
                return sourceInfo;
            }

            // ── 5. KLEI ENGINE (DST, Don't Starve, Oxygen Not Included) ──
            var kleiInfo = CheckKleiEngine(installPath);
            if (kleiInfo != null)
            {
                _logger.LogInformation("Detected Klei Engine at {Path}", installPath);
                return kleiInfo;
            }

            // ── 6. BETHESDA CREATION ENGINE / GAMEBRYO ──
            var creationInfo = CheckCreationEngine(installPath);
            if (creationInfo != null)
            {
                _logger.LogInformation("Detected Creation Engine / Gamebryo at {Path}", installPath);
                return creationInfo;
            }

            // ── 7. PARADOX CLAUSEWITZ / JOMINI ENGINE ──
            var paradoxInfo = CheckParadoxEngine(installPath);
            if (paradoxInfo != null)
            {
                _logger.LogInformation("Detected Paradox Clausewitz / Jomini Engine at {Path}", installPath);
                return paradoxInfo;
            }

            // ── 8. CD PROJEKT RED REDENGINE ──
            var redInfo = CheckRedEngine(installPath);
            if (redInfo != null)
            {
                _logger.LogInformation("Detected REDengine at {Path}", installPath);
                return redInfo;
            }

            // ── 9. RE ENGINE (Capcom) ──
            var reInfo = CheckReEngine(installPath);
            if (reInfo != null)
            {
                _logger.LogInformation("Detected RE Engine at {Path}", installPath);
                return reInfo;
            }

            // ── 10. MT FRAMEWORK (Capcom) ──
            var mtInfo = CheckMtFramework(installPath);
            if (mtInfo != null)
            {
                _logger.LogInformation("Detected MT Framework at {Path}", installPath);
                return mtInfo;
            }

            // ── 11. CRYENGINE / LUMBERYARD ──
            var cryInfo = CheckCryEngine(installPath);
            if (cryInfo != null)
            {
                _logger.LogInformation("Detected CryEngine at {Path}", installPath);
                return cryInfo;
            }

            // ── 12. FROSTBITE ENGINE (EA) ──
            var frostInfo = CheckFrostbite(installPath);
            if (frostInfo != null)
            {
                _logger.LogInformation("Detected Frostbite Engine at {Path}", installPath);
                return frostInfo;
            }

            // ── 13. DECIMA ENGINE ──
            var decimaInfo = CheckDecima(installPath);
            if (decimaInfo != null)
            {
                _logger.LogInformation("Detected Decima Engine at {Path}", installPath);
                return decimaInfo;
            }

            // ── 14. ID TECH ENGINE ──
            var idTechInfo = CheckIdTech(installPath);
            if (idTechInfo != null)
            {
                _logger.LogInformation("Detected id Tech Engine at {Path}", installPath);
                return idTechInfo;
            }

            // ── 15. RPG MAKER ──
            var rpgInfo = CheckRpgMaker(installPath);
            if (rpgInfo != null)
            {
                _logger.LogInformation("Detected RPG Maker at {Path}", installPath);
                return rpgInfo;
            }

            // ── 16. REN'PY ──
            var renpyInfo = CheckRenPy(installPath);
            if (renpyInfo != null)
            {
                _logger.LogInformation("Detected Ren'Py Engine at {Path}", installPath);
                return renpyInfo;
            }

            // ── 17. THE BINDING OF ISAAC / NICALIS CUSTOM ENGINE ──
            var isaacInfo = CheckIsaacEngine(installPath);
            if (isaacInfo != null)
            {
                _logger.LogInformation("Detected Isaac/Nicalis Custom Engine at {Path}", installPath);
                return isaacInfo;
            }

            // ── 18. SUPERGIANT THE FORGE (Hades, Hades II, Transistor, Bastion, Pyre) ──
            var supergiantInfo = CheckSupergiantEngine(installPath);
            if (supergiantInfo != null)
            {
                _logger.LogInformation("Detected Supergiant Engine at {Path}", installPath);
                return supergiantInfo;
            }

            // ── 19. GAMEMAKER ──
            var gmInfo = CheckGameMaker(installPath);
            if (gmInfo != null)
            {
                _logger.LogInformation("Detected GameMaker Engine at {Path}", installPath);
                return gmInfo;
            }

            // ── 18. DEEP BINARY PE INSPECTION FALLBACK ──
            var peInfo = CheckExecutablesMetadata(installPath);
            if (peInfo != null)
            {
                _logger.LogInformation("Detected {Engine} via PE metadata at {Path}", peInfo.Name, installPath);
                return peInfo;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning for engine signatures at {Path}", installPath);
        }

        return CreateGenericEngine();
    }

    /// <inheritdoc />
    public string? FindPrimaryExecutable(string installPath, string? gameName = null)
    {
        var candidates = ShortcutHelper.FindGameExecutables(installPath, gameName);
        return candidates.FirstOrDefault();
    }

    private static EngineInfo CreateGenericEngine() => new()
    {
        Id = "generic",
        Name = "PC Game / Generic",
        Type = EngineType.Generic,
        Capabilities = EngineCapabilities.Mods |
                       EngineCapabilities.Emulation |
                       EngineCapabilities.SteamIntegration |
                       EngineCapabilities.CustomFiles |
                       EngineCapabilities.LaunchArguments |
                       EngineCapabilities.WorkshopSupported
    };

    private static EngineInfo? CheckUnrealEngine(string path)
    {
        try
        {
            var hasEngineDir = Directory.Exists(Path.Combine(path, "Engine")) ||
                               Directory.GetDirectories(path, "Engine", SafeEnumOptions).Length > 0;
            var hasUProject = Directory.GetFiles(path, "*.uproject", SafeEnumOptions).Length > 0;
            var hasShippingExe = Directory.GetFiles(path, "*Shipping.exe", SafeEnumOptions).Length > 0;
            var hasPaks = Directory.GetDirectories(path, "Paks", SafeEnumOptions).Length > 0 ||
                          Directory.GetFiles(path, "*.pak", SafeEnumOptions).Length > 0 ||
                          Directory.GetFiles(path, "*.ucas", SafeEnumOptions).Length > 0;
            var hasBinariesWin64 = Directory.Exists(Path.Combine(path, "Binaries", "Win64")) ||
                                   Directory.GetDirectories(path, "Binaries", SafeEnumOptions).Any(d => d.EndsWith("Win64", StringComparison.OrdinalIgnoreCase));

            if (hasEngineDir || hasUProject || hasShippingExe || (hasBinariesWin64 && hasPaks))
            {
                var version = DetectUnrealVersion(path);
                return new EngineInfo
                {
                    Id = "unreal",
                    Name = "Unreal Engine",
                    Type = EngineType.UnrealEngine,
                    Version = version,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static string? DetectUnrealVersion(string path)
    {
        try
        {
            var buildVersionFile = Directory.GetFiles(path, "Build.version", SafeEnumOptions).FirstOrDefault();
            if (buildVersionFile != null && File.Exists(buildVersionFile))
            {
                var content = File.ReadAllText(buildVersionFile);
                if (content.Contains("\"MajorVersion\": 5"))
                {
                    if (content.Contains("\"MinorVersion\": 4")) return "5.4";
                    if (content.Contains("\"MinorVersion\": 3")) return "5.3";
                    if (content.Contains("\"MinorVersion\": 2")) return "5.2";
                    if (content.Contains("\"MinorVersion\": 1")) return "5.1";
                    if (content.Contains("\"MinorVersion\": 0")) return "5.0";
                    return "5.x";
                }
                if (content.Contains("\"MajorVersion\": 4"))
                {
                    if (content.Contains("\"MinorVersion\": 27")) return "4.27";
                    if (content.Contains("\"MinorVersion\": 26")) return "4.26";
                    if (content.Contains("\"MinorVersion\": 25")) return "4.25";
                    return "4.x";
                }
            }

            var uprojectFile = Directory.GetFiles(path, "*.uproject", SafeEnumOptions).FirstOrDefault();
            if (uprojectFile != null && File.Exists(uprojectFile))
            {
                var content = File.ReadAllText(uprojectFile);
                if (content.Contains("\"EngineAssociation\": \"5.")) return "5.x";
                if (content.Contains("\"EngineAssociation\": \"4.")) return "4.x";
            }

            var exes = Directory.GetFiles(path, "*.exe", SafeEnumOptions);
            foreach (var exe in exes)
            {
                if (!File.Exists(exe)) continue;
                var vi = FileVersionInfo.GetVersionInfo(exe);
                if (vi.CompanyName?.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) == true ||
                    vi.FileDescription?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (vi.ProductVersion?.StartsWith("5.") == true) return "5.x";
                    if (vi.ProductVersion?.StartsWith("4.") == true) return "4.x";
                }
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckUnityEngine(string path)
    {
        try
        {
            var hasUnityPlayer = Directory.GetFiles(path, "UnityPlayer.dll", SafeEnumOptions).Length > 0;
            var hasDataFolder = Directory.GetDirectories(path, "*_Data", SafeEnumOptions).Length > 0;
            var hasGameAssembly = Directory.GetFiles(path, "GameAssembly.dll", SafeEnumOptions).Length > 0;
            var hasUnityCrash = Directory.GetFiles(path, "UnityCrashHandler*.exe", SafeEnumOptions).Length > 0;
            var hasGlobalGameManagers = Directory.GetFiles(path, "globalgamemanagers", SafeEnumOptions).Length > 0;
            var hasDataUnity3d = Directory.GetFiles(path, "data.unity3d", SafeEnumOptions).Length > 0;
            var hasBootConfig = Directory.GetFiles(path, "boot.config", SafeEnumOptions).Length > 0;
            var hasManagedUnityEngine = Directory.GetFiles(path, "UnityEngine*.dll", SafeEnumOptions).Length > 0;

            if (hasUnityPlayer || hasDataFolder || (hasGameAssembly && hasUnityCrash) || hasGlobalGameManagers || hasDataUnity3d || (hasBootConfig && hasManagedUnityEngine))
            {
                var version = DetectUnityVersion(path);
                return new EngineInfo
                {
                    Id = "unity",
                    Name = "Unity",
                    Type = EngineType.Unity,
                    Version = version,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.BepInExSupported |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static string? DetectUnityVersion(string path)
    {
        try
        {
            var playerDll = Directory.GetFiles(path, "UnityPlayer.dll", SafeEnumOptions).FirstOrDefault();
            if (playerDll != null && File.Exists(playerDll))
            {
                var vi = FileVersionInfo.GetVersionInfo(playerDll);
                if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                {
                    var parts = vi.FileVersion.Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var year) && year >= 2017)
                    {
                        return parts.Length > 1 ? $"{parts[0]}.{parts[1]} LTS" : $"{parts[0]} LTS";
                    }
                    return vi.FileVersion;
                }
            }

            var globalGamemanagers = Directory.GetFiles(path, "globalgamemanagers", SafeEnumOptions).FirstOrDefault();
            if (globalGamemanagers != null && File.Exists(globalGamemanagers))
            {
                using var fs = File.OpenRead(globalGamemanagers);
                var buffer = new byte[Math.Min(1024, (int)fs.Length)];
                fs.ReadExactly(buffer, 0, buffer.Length);
                var text = Encoding.ASCII.GetString(buffer);
                var idx = text.IndexOf("20", StringComparison.Ordinal);
                if (idx >= 0 && idx + 8 <= text.Length)
                {
                    var candidate = text.Substring(idx, 8).TrimEnd('\0', ' ', '\r', '\n');
                    if (candidate.Contains('.')) return candidate;
                }
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckGodotEngine(string path)
    {
        try
        {
            // 1. Separate .pck files anywhere in directory tree
            var pckFiles = Directory.GetFiles(path, "*.pck", SafeEnumOptions);
            var hasProjectGodot = Directory.GetFiles(path, "project.godot", SafeEnumOptions).Length > 0 ||
                                  Directory.GetDirectories(path, ".godot", SafeEnumOptions).Length > 0;
            var hasGodotDll = Directory.GetFiles(path, "*godot*.dll", SafeEnumOptions).Length > 0 ||
                              Directory.GetFiles(path, "GodotSharp.dll", SafeEnumOptions).Length > 0;

            if (pckFiles.Length > 0 || hasProjectGodot || hasGodotDll)
            {
                return new EngineInfo
                {
                    Id = "godot",
                    Name = "Godot Engine",
                    Type = EngineType.Godot,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }

            // 2. Embedded GDPC signature or Godot strings in executables
            var exes = Directory.GetFiles(path, "*.exe", SafeEnumOptions);
            foreach (var exe in exes)
            {
                if (HasEmbeddedGodotSignature(exe))
                {
                    return new EngineInfo
                    {
                        Id = "godot",
                        Name = "Godot Engine",
                        Type = EngineType.Godot,
                        Capabilities = EngineCapabilities.Mods |
                                       EngineCapabilities.Emulation |
                                       EngineCapabilities.SteamIntegration |
                                       EngineCapabilities.CustomFiles |
                                       EngineCapabilities.LaunchArguments |
                                       EngineCapabilities.WorkshopSupported
                    };
                }
            }
        }
        catch { }

        return null;
    }

    private static bool HasEmbeddedGodotSignature(string exePath)
    {
        try
        {
            var fi = new FileInfo(exePath);
            if (!fi.Exists || fi.Length < 1024) return false;

            // First, check PE metadata
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            if (vi.ProductName?.Contains("Godot", StringComparison.OrdinalIgnoreCase) == true ||
                vi.FileDescription?.Contains("Godot", StringComparison.OrdinalIgnoreCase) == true ||
                vi.CompanyName?.Contains("Godot Engine", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Read the last 64 KB (where Godot embeds PCK magic 'GDPC' = 0x43504447)
            int readSize = (int)Math.Min(65536L, fs.Length);
            fs.Seek(-readSize, SeekOrigin.End);
            var buffer = new byte[readSize];
            fs.ReadExactly(buffer, 0, readSize);

            // Check Godot 3 and Godot 4 PCK embed footer: [offset 8 bytes][magic 4 bytes GDPC]
            for (int i = 0; i <= buffer.Length - 4; i++)
            {
                // 'G' (0x47), 'D' (0x44), 'P' (0x50), 'C' (0x43)
                if (buffer[i] == 0x47 && buffer[i + 1] == 0x44 && buffer[i + 2] == 0x50 && buffer[i + 3] == 0x43)
                {
                    return true;
                }
            }

            // Fallback: check for ASCII string tokens in the last 64KB
            var tailString = Encoding.ASCII.GetString(buffer);
            if (tailString.Contains("GDScript") ||
                tailString.Contains("Godot Engine") ||
                tailString.Contains("godot_") ||
                tailString.Contains("libgodot"))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static EngineInfo? CheckSourceEngine(string path)
    {
        try
        {
            var hasTier0 = Directory.GetFiles(path, "tier0.dll", SafeEnumOptions).Length > 0 ||
                           Directory.GetFiles(path, "tier0_s.dll", SafeEnumOptions).Length > 0;
            var hasGameInfoTxt = Directory.GetFiles(path, "gameinfo.txt", SafeEnumOptions).Length > 0;
            var hasGameInfoGi = Directory.GetFiles(path, "gameinfo.gi", SafeEnumOptions).Length > 0;
            var hasEngine2Dll = Directory.GetFiles(path, "engine2.dll", SafeEnumOptions).Length > 0 ||
                                Directory.GetFiles(path, "tier0_s64.dll", SafeEnumOptions).Length > 0;
            var hasEngineDll = Directory.GetFiles(path, "engine.dll", SafeEnumOptions).Length > 0;
            var hasVstdlib = Directory.GetFiles(path, "vstdlib*.dll", SafeEnumOptions).Length > 0;
            var hasVpk = Directory.GetFiles(path, "*.vpk", SafeEnumOptions).Length > 0;
            var hasMaterials = Directory.Exists(Path.Combine(path, "materials")) ||
                               Directory.GetDirectories(path, "materials", SafeEnumOptions).Length > 0;

            if (hasGameInfoGi || hasEngine2Dll)
            {
                return new EngineInfo
                {
                    Id = "source2",
                    Name = "Source 2",
                    Type = EngineType.Source2,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }

            if (hasTier0 || hasGameInfoTxt || hasEngineDll || hasVstdlib || (hasVpk && hasMaterials))
            {
                return new EngineInfo
                {
                    Id = "source",
                    Name = "Source Engine",
                    Type = EngineType.Source,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckReEngine(string path)
    {
        try
        {
            var hasChunkPak = Directory.GetFiles(path, "re_chunk_*.pak", SafeEnumOptions).Length > 0;
            var hasReAction = Directory.GetFiles(path, "re_action.dll", SafeEnumOptions).Length > 0;
            var hasNatives = Directory.Exists(Path.Combine(path, "natives")) ||
                             Directory.GetDirectories(path, "natives", SafeEnumOptions).Length > 0;

            if (hasChunkPak || hasReAction || hasNatives)
            {
                return new EngineInfo
                {
                    Id = "re_engine",
                    Name = "RE Engine",
                    Type = EngineType.ReEngine,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckCryEngine(string path)
    {
        try
        {
            var hasCrySystem = Directory.GetFiles(path, "CrySystem.dll", SafeEnumOptions).Length > 0 ||
                               Directory.GetFiles(path, "CryRender*.dll", SafeEnumOptions).Length > 0;
            var hasEnginePak = Directory.GetFiles(path, "engine.pak", SafeEnumOptions).Length > 0;

            if (hasCrySystem || hasEnginePak)
            {
                return new EngineInfo
                {
                    Id = "cryengine",
                    Name = "CryEngine / Lumberyard",
                    Type = EngineType.CryEngine,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckRpgMaker(string path)
    {
        try
        {
            var hasRgss = Directory.GetFiles(path, "*.rgss*a", SafeEnumOptions).Length > 0 ||
                          Directory.GetFiles(path, "RGSS*.dll", SafeEnumOptions).Length > 0;
            var hasRpgData = Directory.GetFiles(path, "System.json", SafeEnumOptions).Length > 0 ||
                             Directory.GetFiles(path, "rpg_core.js", SafeEnumOptions).Length > 0 ||
                             File.Exists(Path.Combine(path, "www", "data", "System.json"));

            if (hasRgss || hasRpgData)
            {
                return new EngineInfo
                {
                    Id = "rpgmaker",
                    Name = "RPG Maker",
                    Type = EngineType.RpgMaker,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckRenPy(string path)
    {
        try
        {
            var hasRenpyDir = Directory.Exists(Path.Combine(path, "renpy")) ||
                              Directory.GetDirectories(path, "renpy", SafeEnumOptions).Length > 0;
            var hasRpa = Directory.GetFiles(path, "*.rpa", SafeEnumOptions).Length > 0 ||
                         Directory.GetFiles(path, "*.rpyb", SafeEnumOptions).Length > 0;

            if (hasRenpyDir || hasRpa)
            {
                return new EngineInfo
                {
                    Id = "renpy",
                    Name = "Ren'Py",
                    Type = EngineType.RenPy,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckGameMaker(string path)
    {
        try
        {
            var hasDataWin = Directory.GetFiles(path, "data.win", SafeEnumOptions).Length > 0 ||
                             Directory.GetFiles(path, "audiogroup*.dat", SafeEnumOptions).Length > 0 ||
                             Directory.GetFiles(path, "Steamworks.gml.dll", SafeEnumOptions).Length > 0;

            if (hasDataWin)
            {
                return new EngineInfo
                {
                    Id = "gamemaker",
                    Name = "GameMaker",
                    Type = EngineType.GameMaker,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckKleiEngine(string path)
    {
        try
        {
            var hasModInfo = Directory.GetFiles(path, "modinfo.lua", SafeEnumOptions).Length > 0;
            var hasModMain = Directory.GetFiles(path, "modmain.lua", SafeEnumOptions).Length > 0;
            var hasKleiScripts = (Directory.GetFiles(path, "main.lua", SafeEnumOptions).Length > 0 ||
                                  Directory.GetFiles(path, "prefabs.lua", SafeEnumOptions).Length > 0) &&
                                 Directory.Exists(Path.Combine(path, "scripts"));
            var hasDataBundles = Directory.Exists(Path.Combine(path, "databundles")) ||
                                 Directory.GetDirectories(path, "databundles", SafeEnumOptions).Length > 0;

            if (hasModInfo || hasModMain || hasKleiScripts || hasDataBundles)
            {
                return new EngineInfo
                {
                    Id = "klei",
                    Name = "Klei Engine",
                    Type = EngineType.Klei,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckCreationEngine(string path)
    {
        try
        {
            var hasEsm = Directory.GetFiles(path, "*.esm", SafeEnumOptions).Length > 0;
            var hasBa2 = Directory.GetFiles(path, "*.ba2", SafeEnumOptions).Length > 0;
            var hasBsa = Directory.GetFiles(path, "*.bsa", SafeEnumOptions).Length > 0;
            var hasEspWithData = Directory.GetFiles(path, "*.esp", SafeEnumOptions).Length > 0 &&
                                 (Directory.Exists(Path.Combine(path, "Data")) || Directory.GetDirectories(path, "Data", SafeEnumOptions).Length > 0);

            if (hasEsm || hasBa2 || hasBsa || hasEspWithData)
            {
                return new EngineInfo
                {
                    Id = "creation_engine",
                    Name = "Creation Engine / Gamebryo",
                    Type = EngineType.CreationEngine,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckParadoxEngine(string path)
    {
        try
        {
            var hasLauncherSettings = Directory.GetFiles(path, "launcher-settings.json", SafeEnumOptions).Length > 0;
            var hasPdxLauncher = Directory.GetFiles(path, "dowser.exe", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(path, "pdx_launcher*", SafeEnumOptions).Length > 0;
            var hasJomini = Directory.GetFiles(path, "*jomini*.dll", SafeEnumOptions).Length > 0 ||
                            Directory.GetFiles(path, "*clausewitz*.dll", SafeEnumOptions).Length > 0;

            if (hasLauncherSettings || hasPdxLauncher || hasJomini)
            {
                return new EngineInfo
                {
                    Id = "paradox",
                    Name = "Clausewitz / Jomini",
                    Type = EngineType.ParadoxClausewitz,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckRedEngine(string path)
    {
        try
        {
            var hasR6 = Directory.Exists(Path.Combine(path, "r6")) || Directory.GetDirectories(path, "r6", SafeEnumOptions).Length > 0;
            var hasRed4Ext = Directory.GetFiles(path, "RED4ext.dll", SafeEnumOptions).Length > 0 ||
                             Directory.GetFiles(path, "redscript.dll", SafeEnumOptions).Length > 0 ||
                             Directory.GetFiles(path, "*redengine*.dll", SafeEnumOptions).Length > 0;
            var hasRedContent = Directory.GetFiles(path, "*.redscripts", SafeEnumOptions).Length > 0 ||
                                Directory.GetFiles(path, "engine\\config\\base.ini", SafeEnumOptions).Length > 0;

            if (hasR6 || hasRed4Ext || hasRedContent)
            {
                return new EngineInfo
                {
                    Id = "redengine",
                    Name = "REDengine",
                    Type = EngineType.RedEngine,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckMtFramework(string path)
    {
        try
        {
            var hasNativePc = Directory.Exists(Path.Combine(path, "nativePC")) ||
                              Directory.GetDirectories(path, "nativePC", SafeEnumOptions).Length > 0;
            var hasArc = Directory.GetFiles(path, "*.arc", SafeEnumOptions).Length > 0;

            if (hasNativePc || hasArc)
            {
                return new EngineInfo
                {
                    Id = "mt_framework",
                    Name = "MT Framework",
                    Type = EngineType.MtFramework,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckFrostbite(string path)
    {
        try
        {
            var hasCas = Directory.GetFiles(path, "cas_*.cas", SafeEnumOptions).Length > 0 ||
                         Directory.GetFiles(path, "*.sb", SafeEnumOptions).Length > 0 ||
                         Directory.GetFiles(path, "*.toc", SafeEnumOptions).Length > 0;

            if (hasCas && (Directory.Exists(Path.Combine(path, "Data")) || Directory.GetDirectories(path, "Data", SafeEnumOptions).Length > 0))
            {
                return new EngineInfo
                {
                    Id = "frostbite",
                    Name = "Frostbite",
                    Type = EngineType.Frostbite,
                    Capabilities = EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckDecima(string path)
    {
        try
        {
            var hasDecimaBin = Directory.GetFiles(path, "Dx12\\*.bin", SafeEnumOptions).Length > 0 ||
                               Directory.GetFiles(path, "Core\\*.bin", SafeEnumOptions).Length > 0;
            var hasOodleCore = Directory.GetFiles(path, "oo2core_*.dll", SafeEnumOptions).Length > 0;

            if (hasDecimaBin && hasOodleCore)
            {
                return new EngineInfo
                {
                    Id = "decima",
                    Name = "Decima Engine",
                    Type = EngineType.Decima,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckIdTech(string path)
    {
        try
        {
            var hasIdTechDll = Directory.GetFiles(path, "idTech*.dll", SafeEnumOptions).Length > 0;
            var hasIdTechBase = (Directory.GetFiles(path, "*.streamed", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(path, "*.resources", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(path, "*.pk4", SafeEnumOptions).Length > 0) &&
                                (Directory.Exists(Path.Combine(path, "base")) || Directory.GetDirectories(path, "base", SafeEnumOptions).Length > 0);

            if (hasIdTechDll || hasIdTechBase)
            {
                return new EngineInfo
                {
                    Id = "idtech",
                    Name = "id Tech",
                    Type = EngineType.IdTech,
                    Capabilities = EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.LaunchArguments
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckExecutablesMetadata(string path)
    {
        try
        {
            var exes = Directory.GetFiles(path, "*.exe", SafeEnumOptions);
            foreach (var exe in exes)
            {
                if (!File.Exists(exe)) continue;
                var vi = FileVersionInfo.GetVersionInfo(exe);

                if (vi.CompanyName?.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) == true ||
                    vi.FileDescription?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new EngineInfo
                    {
                        Id = "unreal",
                        Name = "Unreal Engine",
                        Type = EngineType.UnrealEngine,
                        Capabilities = EngineCapabilities.Mods | EngineCapabilities.Emulation | EngineCapabilities.SteamIntegration | EngineCapabilities.WorkshopSupported
                    };
                }

                if (vi.CompanyName?.Contains("Unity Technologies", StringComparison.OrdinalIgnoreCase) == true ||
                    vi.FileDescription?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new EngineInfo
                    {
                        Id = "unity",
                        Name = "Unity",
                        Type = EngineType.Unity,
                        Capabilities = EngineCapabilities.Mods | EngineCapabilities.Emulation | EngineCapabilities.BepInExSupported | EngineCapabilities.SteamIntegration | EngineCapabilities.WorkshopSupported
                    };
                }

                if (vi.ProductName?.Contains("Godot", StringComparison.OrdinalIgnoreCase) == true ||
                    vi.FileDescription?.Contains("Godot", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new EngineInfo
                    {
                        Id = "godot",
                        Name = "Godot Engine",
                        Type = EngineType.Godot,
                        Capabilities = EngineCapabilities.Mods | EngineCapabilities.Emulation | EngineCapabilities.SteamIntegration | EngineCapabilities.CustomFiles | EngineCapabilities.WorkshopSupported
                    };
                }
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckIsaacEngine(string path)
    {
        try
        {
            var isIsaac = Directory.GetFiles(path, "isaac-ng*.exe", SafeEnumOptions).Length > 0 ||
                          Directory.GetFiles(path, "*binding*isaac*.exe", SafeEnumOptions).Length > 0 ||
                          File.Exists(Path.Combine(path, "isaac-ng.exe")) ||
                          (Directory.Exists(Path.Combine(path, "resources", "packed")) &&
                           Directory.GetFiles(Path.Combine(path, "resources", "packed"), "*.a", SafeEnumOptions).Length > 0);

            if (isIsaac)
            {
                return new EngineInfo
                {
                    Id = "isaac-custom",
                    Name = "Custom Engine (Isaac)",
                    Type = EngineType.Custom,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }

    private static EngineInfo? CheckSupergiantEngine(string path)
    {
        try
        {
            var hasSupergiantExe = Directory.GetFiles(path, "Hades*.exe", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Transistor*.exe", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Bastion*.exe", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Pyre*.exe", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Pyre*.bin", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Engine.Win64.dll", SafeEnumOptions).Length > 0 ||
                                   Directory.GetFiles(path, "Engine.dll", SafeEnumOptions).Length > 0;

            var hasSupergiantPackages = (Directory.Exists(Path.Combine(path, "Content", "Packages")) &&
                                         Directory.GetFiles(Path.Combine(path, "Content", "Packages"), "*.pkg", SafeEnumOptions).Length > 0) ||
                                        (Directory.Exists(Path.Combine(path, "Packages")) &&
                                         Directory.GetFiles(Path.Combine(path, "Packages"), "*.pkg", SafeEnumOptions).Length > 0);

            var hasSupergiantScripts = Directory.Exists(Path.Combine(path, "Content", "Scripts")) &&
                                       (File.Exists(Path.Combine(path, "Content", "Scripts", "RoomManager.lua")) ||
                                        File.Exists(Path.Combine(path, "Content", "Scripts", "Combat.lua")) ||
                                        File.Exists(Path.Combine(path, "Content", "Scripts", "TraitData.lua")) ||
                                        File.Exists(Path.Combine(path, "Content", "Scripts", "GameData.lua")));

            if (hasSupergiantExe || hasSupergiantPackages || hasSupergiantScripts)
            {
                return new EngineInfo
                {
                    Id = "supergiant",
                    Name = "Supergiant Engine",
                    Type = EngineType.Supergiant,
                    Capabilities = EngineCapabilities.Mods |
                                   EngineCapabilities.Emulation |
                                   EngineCapabilities.SteamIntegration |
                                   EngineCapabilities.CustomFiles |
                                   EngineCapabilities.LaunchArguments |
                                   EngineCapabilities.WorkshopSupported
                };
            }
        }
        catch { }

        return null;
    }
}
