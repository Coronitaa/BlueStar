using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements game prerequisite discovery and 1-click silent installation for Windows games.
/// </summary>
#pragma warning disable CA1416
[SupportedOSPlatform("windows")]
public sealed class PrerequisiteService : IPrerequisiteService
{
    private readonly HttpClient _http;
    private readonly ILogger<PrerequisiteService> _logger;

    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        MaxRecursionDepth = 5,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    public PrerequisiteService(HttpClient http, ILogger<PrerequisiteService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PrerequisiteItem>> DetectSystemPrerequisitesAsync(CancellationToken ct = default)
    {
        var items = new List<PrerequisiteItem>
        {
            new()
            {
                Id = "vcredist_2015_2022_x64",
                Name = "Visual C++ 2015-2022 Redistributable (x64)",
                Category = "Visual C++",
                Description = "Required by BlueStar, modern 64-bit Windows games, and injectors (C++ standard libraries & MSVCP140.dll).",
                DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true,
                IsRecognized = true
            },
            new()
            {
                Id = "vcredist_2015_2022_x86",
                Name = "Visual C++ 2015-2022 Redistributable (x86)",
                Category = "Visual C++",
                Description = "Required by 32-bit components, legacy game binaries, and Steam overlay hooks.",
                DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true,
                IsRecognized = true
            },
            new()
            {
                Id = "directx_enduser",
                Name = "DirectX End-User Runtime (Legacy D3DX/XAudio2)",
                Category = "DirectX",
                Description = "Provides legacy DirectX 9.0c, D3DX9, D3DCompiler, and XAudio2 DLLs essential for games.",
                DownloadUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe",
                SilentArguments = "/Q",
                IsEssential = true,
                IsRecognized = true
            },
            new()
            {
                Id = "dotnet_desktop_8",
                Name = ".NET Desktop Runtime 8.0 (x64)",
                Category = ".NET Runtime",
                Description = "Required for C# and modern Unity/.NET game frameworks and mods.",
                DownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = false,
                IsRecognized = true
            },
            new()
            {
                Id = "dotnet_runtime_8",
                Name = ".NET Runtime 8.0 (x64)",
                Category = ".NET Runtime",
                Description = "Required for DepotDownloader and multi-manifest game downloads.",
                DownloadUrl = "https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true,
                IsRecognized = true
            },
            new()
            {
                Id = "webview2_runtime",
                Name = "Microsoft Edge WebView2 Runtime",
                Category = "Web Browser Engine",
                Description = "Required for embedded Steam Store, SteamDB, and web navigation panels in BlueStar.",
                DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                SilentArguments = "/silent /install",
                IsEssential = true,
                IsRecognized = true
            }
        };

        foreach (var item in items)
        {
            item.Status = IsSystemInstalled(item)
                ? PrerequisiteStatus.InstalledInSystem
                : PrerequisiteStatus.NeedsDownload;
        }

        return Task.FromResult<IReadOnlyList<PrerequisiteItem>>(items);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrerequisiteItem>> DetectPrerequisitesAsync(GameInstance instance, CancellationToken ct = default)
    {
        var systemItems = await DetectSystemPrerequisitesAsync(ct).ConfigureAwait(false);
        var items = new List<PrerequisiteItem>(systemItems);

        var vc2015_2022_x64 = items.FirstOrDefault(i => i.Id == "vcredist_2015_2022_x64");
        var vc2015_2022_x86 = items.FirstOrDefault(i => i.Id == "vcredist_2015_2022_x86");
        var directx = items.FirstOrDefault(i => i.Id == "directx_enduser");

        PrerequisiteItem? uePrereq = null;
        if (instance.Engine?.Type == EngineType.UnrealEngine)
        {
            uePrereq = new PrerequisiteItem
            {
                Id = "ue_prereqs_x64",
                Name = "Unreal Engine Prerequisites (x64)",
                Category = "Unreal Engine",
                Description = "Includes DirectX, Visual C++, and Epic Games runtime components for Unreal Engine titles.",
                DownloadUrl = null,
                SilentArguments = "/quiet /norestart",
                IsEssential = true,
                IsRecognized = true,
                Status = IsSystemInstalled(new PrerequisiteItem { Id = "ue_prereqs_x64", Name = "UE Prereqs" }) ? PrerequisiteStatus.InstalledInSystem : PrerequisiteStatus.NeedsDownload
            };
            items.Add(uePrereq);
        }

        // Scan the local game directory for embedded installers.
        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            try
            {
                var files = Directory.GetFiles(instance.InstallPath, "*.exe", SafeEnumOptions)
                    .Concat(Directory.GetFiles(instance.InstallPath, "*.msi", SafeEnumOptions))
                    .ToList();

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file).ToLowerInvariant();
                    var claimed = true;

                    if (fileName.Contains("vcredist") || fileName.Contains("vc_redist"))
                    {
                        if (fileName.Contains("x64") || fileName.Contains("64"))
                        {
                            if (vc2015_2022_x64 != null)
                            {
                                vc2015_2022_x64.LocalInstallerPath = file;
                                if (vc2015_2022_x64.Status != PrerequisiteStatus.InstalledInSystem)
                                    vc2015_2022_x64.Status = PrerequisiteStatus.AvailableInGame;
                            }
                            else
                            {
                                AddDiscovered(items, file, PrettyInstallerName(file), "Visual C++",
                                    "Visual C++ Redistributable 64-bit runtime.",
                                    silentArgs: "/install /quiet /norestart", essential: true, isRecognized: true);
                            }
                        }
                        else if (fileName.Contains("x86") || fileName.Contains("32"))
                        {
                            if (vc2015_2022_x86 != null)
                            {
                                vc2015_2022_x86.LocalInstallerPath = file;
                                if (vc2015_2022_x86.Status != PrerequisiteStatus.InstalledInSystem)
                                    vc2015_2022_x86.Status = PrerequisiteStatus.AvailableInGame;
                            }
                            else
                            {
                                AddDiscovered(items, file, PrettyInstallerName(file), "Visual C++",
                                    "Visual C++ Redistributable 32-bit runtime.",
                                    silentArgs: "/install /quiet /norestart", essential: true, isRecognized: true);
                            }
                        }
                    }
                    else if (fileName.Equals("dxsetup.exe") || fileName.Contains("dxsetup") || fileName.Contains("dxwebsetup"))
                    {
                        if (directx != null)
                        {
                            directx.LocalInstallerPath = file;
                            if (directx.Status != PrerequisiteStatus.InstalledInSystem)
                                directx.Status = PrerequisiteStatus.AvailableInGame;
                        }
                    }
                    else if (fileName.Contains("ue4prereq") || fileName.Contains("ue5prereq") || fileName.Contains("ueprereq"))
                    {
                        if (uePrereq != null)
                        {
                            uePrereq.LocalInstallerPath = file;
                            if (uePrereq.Status != PrerequisiteStatus.InstalledInSystem)
                                uePrereq.Status = PrerequisiteStatus.AvailableInGame;
                        }
                        else
                        {
                            AddDiscovered(items, file, "Unreal Engine Prerequisites", "Unreal Engine",
                                "Visual C++, DirectX and Epic runtime components shipped with this Unreal Engine title.",
                                silentArgs: "/quiet /norestart", essential: true, isRecognized: true);
                        }
                    }
                    else if (fileName.Contains("physx"))
                    {
                        var isMsi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                        AddDiscovered(items, file, "NVIDIA PhysX System Software", "PhysX",
                            "Hardware-accelerated physics engine for legacy and Unreal games.",
                            id: "nvidia_physx",
                            silentArgs: isMsi ? "/qn /norestart" : "/quiet /norestart",
                            isRecognized: true);
                    }
                    else if (fileName.Contains("oalinst") || fileName.Contains("openal"))
                    {
                        AddDiscovered(items, file, "OpenAL 3D Audio Runtime", "OpenAL",
                            "Cross-platform 3D positional audio API.",
                            id: "openal_runtime",
                            silentArgs: "/s",
                            isRecognized: true);
                    }
                    else if (fileName.Contains("xnafx") || fileName.Contains("xna"))
                    {
                        var isMsi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                        AddDiscovered(items, file, "Microsoft XNA Framework Redistributable", "XNA",
                            "Runtime required by XNA / MonoGame titles.",
                            id: "xna_framework",
                            silentArgs: isMsi ? "/qn /norestart" : "/quiet /norestart",
                            isRecognized: true);
                    }
                    else if (fileName.Contains("dotnetfx") || fileName.StartsWith("ndp") || fileName.Contains("netfx"))
                    {
                        AddDiscovered(items, file, ".NET Framework Redistributable", ".NET Runtime",
                            "Legacy .NET Framework runtime shipped with this title.",
                            id: "dotnet_framework_legacy",
                            silentArgs: "/q /norestart",
                            isRecognized: true);
                    }
                    else if (fileName.Contains("directx") || fileName.Contains("d3d"))
                    {
                        AddDiscovered(items, file, "DirectX Redistributable (bundled)", "DirectX",
                            "DirectX runtime files shipped inside the game package.",
                            id: "directx_bundled",
                            silentArgs: "/silent",
                            isRecognized: true);
                    }
                    else
                    {
                        claimed = false;
                    }

                    if (claimed) continue;

                    // Unanticipated installers: require informed user consent before execution
                    if (IsInRedistributableFolder(file, instance.InstallPath))
                    {
                        AddDiscovered(items, file, PrettyInstallerName(file), "Unverified Package",
                            "Discovered third-party installer bundled with game package. Requires manual confirmation before execution.",
                            silentArgs: string.Empty,
                            essential: false,
                            isRecognized: false,
                            requiresExplicitConsent: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning local game directory for prerequisites in {Path}", instance.InstallPath);
            }
        }

        return items;
    }

    private static readonly string[] RedistFolderNames =
    [
        "_commonredist", "commonredist", "_redist", "redist", "redists", "redistributable",
        "redistributables", "prereq", "prereqs", "prerequisite", "prerequisites",
        "directx", "vcredist", "installers", "install", "support", "extras", "runtime", "runtimes"
    ];

    private static readonly string[] NonPrerequisiteNames =
    [
        "unins", "uninstall", "setup.exe", "launcher", "crashreport", "crashhandler",
        "epicwebhelper", "steamservice", "quickboot", "eac", "battleye", "activate"
    ];

    private static bool IsInRedistributableFolder(string file, string installRoot)
    {
        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(directory)) return false;

        var fileName = Path.GetFileName(file).ToLowerInvariant();
        if (NonPrerequisiteNames.Any(n => fileName.Contains(n, StringComparison.Ordinal))) return false;

        var relative = Path.GetRelativePath(installRoot, directory);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") return false;

        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => RedistFolderNames.Contains(segment.ToLowerInvariant()));
    }

    private static string PrettyInstallerName(string file)
    {
        var raw = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace('-', ' ').Trim();
        if (string.IsNullOrWhiteSpace(raw)) return Path.GetFileName(file);

        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..] : w.ToUpperInvariant());

        var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
        var name = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(parent) ? name : $"{name} ({parent})";
    }

    private static void AddDiscovered(
        List<PrerequisiteItem> items,
        string file,
        string name,
        string category,
        string description,
        string? id = null,
        string? silentArgs = null,
        bool essential = false,
        bool isRecognized = true,
        bool requiresExplicitConsent = false)
    {
        id ??= "pkg_" + Path.GetFileNameWithoutExtension(file).ToLowerInvariant()
            .Replace(' ', '_').Replace('-', '_').Replace('.', '_');

        if (items.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))) return;

        silentArgs ??= file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? "/qn /norestart"
            : "/quiet /norestart";

        items.Add(new PrerequisiteItem
        {
            Id = id,
            Name = name,
            Category = category,
            Description = description,
            LocalInstallerPath = file,
            Status = PrerequisiteStatus.AvailableInGame,
            SilentArguments = silentArgs,
            IsEssential = essential,
            IsRecognized = isRecognized,
            RequiresExplicitConsent = requiresExplicitConsent
        });
    }

    /// <inheritdoc />
    public bool IsSystemInstalled(PrerequisiteItem item)
    {
        try
        {
            if (item.Id == "vcredist_2015_2022_x64")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
                if (key != null)
                {
                    var installed = key.GetValue("Installed");
                    var major = key.GetValue("Major");
                    var bld = key.GetValue("Bld");

                    if (installed is int instVal && instVal == 1 &&
                        major is int majVal && majVal >= 14 &&
                        bld is int bldVal && bldVal >= 24215)
                    {
                        return true;
                    }
                }
                return false;
            }
            else if (item.Id == "vcredist_2015_2022_x86")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");
                if (key != null)
                {
                    var installed = key.GetValue("Installed");
                    var major = key.GetValue("Major");
                    if (installed is int instVal && instVal == 1 && major is int majVal && majVal >= 14)
                    {
                        return true;
                    }
                }
                return false;
            }
            else if (item.Id == "directx_enduser")
            {
                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(sys32, "d3dx9_43.dll")) && File.Exists(Path.Combine(sys32, "d3dcompiler_43.dll")))
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
                    if (key?.GetValue("Version") is string version && !string.IsNullOrWhiteSpace(version))
                        return true;
                    if (key != null) return true;
                }
                return false;
            }
            else if (item.Id == "dotnet_desktop_8")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                if (key?.GetValue("Version") is string ver && ver.StartsWith("8."))
                    return true;

                var dotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(dotnetDir) && Directory.GetDirectories(dotnetDir, "8.*").Length > 0)
                    return true;
            }
            else if (item.Id == "dotnet_runtime_8")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App");
                if (key?.GetValue("Version") is string ver && ver.StartsWith("8."))
                    return true;

                var dotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(dotnetDir) && Directory.GetDirectories(dotnetDir, "8.*").Length > 0)
                    return true;
            }
            else if (item.Id == "webview2_runtime")
            {
                const string clientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
                using var keyWow64 = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{clientGuid}");
                if (keyWow64?.GetValue("pv") is string pv1 && !string.IsNullOrWhiteSpace(pv1) && pv1 != "0.0.0.0")
                    return true;

                using var keyHklm = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{clientGuid}");
                if (keyHklm?.GetValue("pv") is string pv2 && !string.IsNullOrWhiteSpace(pv2) && pv2 != "0.0.0.0")
                    return true;

                using var keyHkcu = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{clientGuid}");
                if (keyHkcu?.GetValue("pv") is string pv3 && !string.IsNullOrWhiteSpace(pv3) && pv3 != "0.0.0.0")
                    return true;

                return false;
            }
            else if (item.Id == "ue_prereqs_x64")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine Prerequisites (x64)")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\Unreal Engine Prerequisites (x64)");
                if (key != null) return true;

                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(sys32, "msvcp140.dll")) && File.Exists(Path.Combine(sys32, "d3dcompiler_47.dll")))
                    return true;
            }
        }
        catch { }

        return false;
    }

    /// <inheritdoc />
    public Task<bool> InstallPrerequisiteAsync(GameInstance instance, PrerequisiteItem item, IProgress<string>? progress = null, CancellationToken ct = default)
        => InstallPrerequisiteAsync(item, progress, ct);

    /// <inheritdoc />
    /// <summary>
    /// Offline installers bundled with release builds, by prerequisite id.
    /// </summary>
    private static readonly Dictionary<string, string> BundledInstallers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vcredist_2015_2022_x64"] = "vc_redist.x64.exe",
        ["vcredist_2015_2022_x86"] = "vc_redist.x86.exe",
        ["webview2_runtime"] = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
    };

    internal static string? BundledInstallerPath(string? id, string? baseDirectory = null)
    {
        if (id == null || !BundledInstallers.TryGetValue(id, out var fileName)) return null;
        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "prerequisites", fileName);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> InstallPrerequisiteAsync(PrerequisiteItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Status = PrerequisiteStatus.Installing;
        progress?.Report($"Starting installation of {item.Name}...");

        string? installerPath = item.LocalInstallerPath;

        // Release builds ship the offline installers in {app}\prerequisites; prefer them over downloading
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            var bundled = BundledInstallerPath(item.Id);
            if (bundled != null)
            {
                installerPath = bundled;
                item.LocalInstallerPath = bundled;
            }
        }

        // Download if local installer is not available
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                progress?.Report($"❌ No local installer or download URL found for {item.Name}.");
                item.Status = PrerequisiteStatus.InstallFailed;
                return false;
            }

            try
            {
                var prereqDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "prerequisites");
                Directory.CreateDirectory(prereqDir);

                var ext = Path.GetExtension(new Uri(item.DownloadUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".exe";
                var targetFile = Path.Combine(prereqDir, $"{item.Id}{ext}");

                if (!File.Exists(targetFile) || new FileInfo(targetFile).Length < 1024)
                {
                    progress?.Report($"⏳ Downloading {item.Name} from official Microsoft servers...");
                    _logger.LogInformation("Downloading prerequisite from {Url} to {Dest}", item.DownloadUrl, targetFile);

                    using var response = await _http.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await contentStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }

                installerPath = targetFile;
                item.LocalInstallerPath = targetFile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download prerequisite {Id}", item.Id);
                progress?.Report($"❌ Failed to download {item.Name}: {ex.Message}");
                item.Status = PrerequisiteStatus.InstallFailed;
                return false;
            }
        }

        try
        {
            bool isMsi = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            string exeName;
            string args;

            if (item.RequiresExplicitConsent && !item.IsRecognized)
            {
                // Unrecognized installer: launch interactively without forcing silent flags
                progress?.Report($"🖥️ Launching installer for {item.Name}. Please complete the setup wizard.");
                _logger.LogInformation("Launching unverified installer interactively: {Path}", installerPath);

                if (isMsi)
                {
                    exeName = "msiexec.exe";
                    args = $"/i \"{installerPath}\"";
                }
                else
                {
                    exeName = installerPath;
                    args = string.Empty;
                }
            }
            else
            {
                // Verified prerequisite: apply appropriate silent unattended arguments
                progress?.Report($"⚙️ Installing {item.Name} silently...");
                _logger.LogInformation("Launching verified prerequisite installer: {Path} with arguments: {Args}", installerPath, item.SilentArguments);

                if (isMsi)
                {
                    exeName = "msiexec.exe";
                    var silentArgs = string.IsNullOrWhiteSpace(item.SilentArguments) ? "/qn /norestart" : item.SilentArguments;
                    args = $"/i \"{installerPath}\" {silentArgs}";
                }
                else
                {
                    exeName = installerPath;
                    args = item.SilentArguments ?? string.Empty;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = exeName,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report($"❌ Could not start installer process for {item.Name}.");
                item.Status = PrerequisiteStatus.InstallFailed;
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // Exit code check:
            // 0 = Success
            // 3010 = Success, reboot required
            // 1641 = Success, reboot initiated
            // 1638 = Newer/same version already installed
            // 5100 = Newer version installed
            var isInstalled = IsSystemInstalled(item);
            bool isSuccessCode = process.ExitCode is 0 or 3010 or 1641 or 1638 or 5100;

            if (isSuccessCode || isInstalled)
            {
                var rebootNote = process.ExitCode == 3010 ? " (Reboot required)" : string.Empty;
                progress?.Report($"✅ {item.Name} installed successfully{rebootNote} (Exit code: {process.ExitCode}).");
                item.Status = PrerequisiteStatus.InstalledSuccess;
                return true;
            }
            else
            {
                progress?.Report($"⚠️ Installer for {item.Name} finished with exit code {process.ExitCode}.");
                item.Status = isInstalled ? PrerequisiteStatus.InstalledSuccess : PrerequisiteStatus.InstallFailed;
                return item.Status == PrerequisiteStatus.InstalledSuccess;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing prerequisite installer for {Id}", item.Id);
            progress?.Report($"❌ Error executing installation of {item.Name}: {ex.Message}");
            item.Status = PrerequisiteStatus.InstallFailed;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> InstallAllPrerequisitesAsync(IEnumerable<PrerequisiteItem> items, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int installedCount = 0;
        int skippedUnverified = 0;

        foreach (var p in items)
        {
            if (p.RequiresExplicitConsent && !p.IsRecognized)
            {
                skippedUnverified++;
                continue;
            }

            if (p.Status != PrerequisiteStatus.InstalledInSystem && p.Status != PrerequisiteStatus.InstalledSuccess)
            {
                var success = await InstallPrerequisiteAsync(p, progress, ct).ConfigureAwait(false);
                if (success) installedCount++;
            }
        }

        if (skippedUnverified > 0)
        {
            progress?.Report($"ℹ️ Skipped {skippedUnverified} unverified third-party package(s). You can trigger them individually with manual confirmation.");
        }

        progress?.Report($"🎉 Prerequisite installation completed ({installedCount} component(s) configured).");
        return installedCount;
    }

    /// <inheritdoc />
    public async Task<int> InstallAllPrerequisitesAsync(GameInstance instance, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var prereqs = await DetectPrerequisitesAsync(instance, ct).ConfigureAwait(false);
        return await InstallAllPrerequisitesAsync(prereqs, progress, ct).ConfigureAwait(false);
    }
}
