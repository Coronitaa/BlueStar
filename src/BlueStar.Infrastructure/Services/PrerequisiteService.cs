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
                Description = "Required by almost all modern 64-bit Windows games and injectors (C++ standard libraries & MSVCP140.dll).",
                DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true
            },
            new()
            {
                Id = "vcredist_2015_2022_x86",
                Name = "Visual C++ 2015-2022 Redistributable (x86)",
                Category = "Visual C++",
                Description = "Required by 32-bit components, launchers, and Steam overlay hooks.",
                DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true
            },
            new()
            {
                Id = "directx_enduser",
                Name = "DirectX End-User Runtime (Legacy D3DX/XAudio2)",
                Category = "DirectX",
                Description = "Provides legacy DirectX 9.0c, D3DX9, D3DCompiler, and XAudio2 DLLs essential for games.",
                DownloadUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe",
                SilentArguments = "/Q",
                IsEssential = true
            },
            new()
            {
                Id = "dotnet_desktop_8",
                Name = ".NET Desktop Runtime 8.0 (x64)",
                Category = ".NET Runtime",
                Description = "Required for C# and modern Unity/.NET game frameworks and mods.",
                DownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = false
            },
            new()
            {
                Id = "dotnet_runtime_9",
                Name = ".NET Runtime 9.0 (x64)",
                Category = ".NET Runtime",
                Description = "Required for DepotDownloader and multi-manifest game downloads.",
                DownloadUrl = "https://aka.ms/dotnet/9.0/dotnet-runtime-win-x64.exe",
                SilentArguments = "/install /quiet /norestart",
                IsEssential = true
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
                Status = IsSystemInstalled(new PrerequisiteItem { Id = "ue_prereqs_x64", Name = "UE Prereqs" }) ? PrerequisiteStatus.InstalledInSystem : PrerequisiteStatus.NeedsDownload
            };
            items.Add(uePrereq);
        }

        // 3. Scan the local game directory for embedded installers.
        //    Two passes: the redistributables we know how to recognise, and then anything else
        //    shipped in a redist folder, so a title that bundles a prerequisite we never
        //    anticipated still shows up instead of being silently skipped.
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
                        }
                        else if (fileName.Contains("x86") || fileName.Contains("32"))
                        {
                            if (vc2015_2022_x86 != null)
                            {
                                vc2015_2022_x86.LocalInstallerPath = file;
                                if (vc2015_2022_x86.Status != PrerequisiteStatus.InstalledInSystem)
                                    vc2015_2022_x86.Status = PrerequisiteStatus.AvailableInGame;
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
                            // The engine was not detected but the package ships the UE prereqs anyway.
                            AddDiscovered(items, file, "Unreal Engine Prerequisites", "Unreal Engine",
                                "Visual C++, DirectX and Epic runtime components shipped with this Unreal Engine title.");
                        }
                    }
                    else if (fileName.Contains("physx"))
                    {
                        AddDiscovered(items, file, "NVIDIA PhysX System Software", "PhysX",
                            "Hardware-accelerated physics engine for legacy and Unreal games.", id: "nvidia_physx");
                    }
                    else if (fileName.Contains("oalinst") || fileName.Contains("openal"))
                    {
                        AddDiscovered(items, file, "OpenAL 3D Audio Runtime", "OpenAL",
                            "Cross-platform 3D positional audio API.", id: "openal_runtime", silentArgs: "/s");
                    }
                    else if (fileName.Contains("xnafx") || fileName.Contains("xna"))
                    {
                        AddDiscovered(items, file, "Microsoft XNA Framework Redistributable", "XNA",
                            "Runtime required by XNA / MonoGame titles.", id: "xna_framework");
                    }
                    else if (fileName.Contains("dotnetfx") || fileName.StartsWith("ndp") || fileName.Contains("netfx"))
                    {
                        AddDiscovered(items, file, ".NET Framework Redistributable", ".NET Runtime",
                            "Legacy .NET Framework runtime shipped with this title.", id: "dotnet_framework_legacy");
                    }
                    else if (fileName.Contains("directx") || fileName.Contains("d3d"))
                    {
                        AddDiscovered(items, file, "DirectX Redistributable (bundled)", "DirectX",
                            "DirectX runtime files shipped inside the game package.", id: "directx_bundled");
                    }
                    else
                    {
                        claimed = false;
                    }

                    if (claimed) continue;

                    // ── Unanticipated prerequisites ──
                    // Anything left that lives in a redistributable folder is treated as a
                    // prerequisite of this specific game, whatever it happens to be.
                    if (IsInRedistributableFolder(file, instance.InstallPath))
                    {
                        AddDiscovered(items, file, PrettyInstallerName(file), "Game Package",
                            "Bundled with this game as a required runtime component.",
                            essential: false);
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

    /// <summary>
    /// Folder names publishers use for the runtimes they ship next to the game. Anything found
    /// inside one is a prerequisite by intent, even when its file name means nothing to us.
    /// </summary>
    private static readonly string[] RedistFolderNames =
    [
        "_commonredist", "commonredist", "_redist", "redist", "redists", "redistributable",
        "redistributables", "prereq", "prereqs", "prerequisite", "prerequisites",
        "directx", "vcredist", "installers", "install", "support", "extras", "runtime", "runtimes"
    ];

    /// <summary>
    /// Executable names that are the game itself, an uninstaller or a crash reporter rather than
    /// a runtime, and which must never be launched as a "prerequisite".
    /// </summary>
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

        // The game root itself is not a redist folder: only nested redist folders count.
        var relative = Path.GetRelativePath(installRoot, directory);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") return false;

        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => RedistFolderNames.Contains(segment.ToLowerInvariant()));
    }

    /// <summary>Turns "vcredist_x64_2013.exe" into something a person can read in the list.</summary>
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
        bool essential = false)
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
            IsEssential = essential
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
                if (key?.GetValue("Installed") is int val && val == 1) return true;

                // Also check if msvcp140.dll and vcruntime140.dll exist in System32
                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(sys32, "msvcp140.dll")) && File.Exists(Path.Combine(sys32, "vcruntime140.dll")))
                    return true;
            }
            else if (item.Id == "vcredist_2015_2022_x86")
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X86")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");
                if (key?.GetValue("Installed") is int val && val == 1) return true;

                var sysWow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
                if (File.Exists(Path.Combine(sysWow64, "msvcp140.dll")) && File.Exists(Path.Combine(sysWow64, "vcruntime140.dll")))
                    return true;
            }
            else if (item.Id == "directx_enduser")
            {
                // Verify presence of critical DirectX libraries (d3dx9_43.dll, d3dcompiler_47.dll, xaudio2_7.dll)
                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(sys32, "d3dx9_43.dll")) && File.Exists(Path.Combine(sys32, "d3dcompiler_47.dll")))
                    return true;
            }
            else if (item.Id == "dotnet_desktop_8")
            {
                var dotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(dotnetDir) && Directory.GetDirectories(dotnetDir, "8.*").Length > 0)
                    return true;
            }
            else if (item.Id == "dotnet_runtime_9")
            {
                var dotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(dotnetDir) && Directory.GetDirectories(dotnetDir, "9.*").Length > 0)
                    return true;
            }
            else if (item.Id == "ue_prereqs_x64")
            {
                // Check if VC++ 2015-2022 and DirectX are installed (which covers UE prereqs)
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
    public async Task<bool> InstallPrerequisiteAsync(PrerequisiteItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Status = PrerequisiteStatus.Installing;
        progress?.Report($"Starting installation of {item.Name}...");

        string? installerPath = item.LocalInstallerPath;

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

        // Execute installer interactively so user can complete the installation wizard
        try
        {
            progress?.Report($"🖥️ Opening installer for {item.Name}... Please complete the wizard on screen.");
            _logger.LogInformation("Launching interactive prerequisite installer: {Path}", installerPath);

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = string.Empty,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report($"❌ Could not start installer for {item.Name}.");
                item.Status = PrerequisiteStatus.InstallFailed;
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // Exit code 0 = Success, 3010 / 1641 = Success with reboot required
            var isInstalled = IsSystemInstalled(item);
            if (process.ExitCode == 0 || process.ExitCode == 3010 || process.ExitCode == 1641 || isInstalled)
            {
                progress?.Report($"✅ {item.Name} installed successfully (Exit code: {process.ExitCode}).");
                item.Status = PrerequisiteStatus.InstalledSuccess;
                return true;
            }
            else
            {
                progress?.Report($"⚠️ Wizard for {item.Name} exited with code {process.ExitCode}.");
                item.Status = isInstalled ? PrerequisiteStatus.InstalledSuccess : PrerequisiteStatus.AvailableInGame;
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

        foreach (var p in items)
        {
            if (p.Status != PrerequisiteStatus.InstalledInSystem && p.Status != PrerequisiteStatus.InstalledSuccess)
            {
                var success = await InstallPrerequisiteAsync(p, progress, ct).ConfigureAwait(false);
                if (success) installedCount++;
            }
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
