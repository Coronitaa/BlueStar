using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Heuristic mod dispatcher that inspects mod payload contents (.pak, .vpk, .dll, .json)
/// and deploys them to the optimal game engine location.
/// </summary>
public sealed class HeuristicModDispatcher : IHeuristicModDispatcher
{
    private readonly IWin32Linker _linker;
    private readonly ILogger<HeuristicModDispatcher> _logger;

    public HeuristicModDispatcher(IWin32Linker linker, ILogger<HeuristicModDispatcher> logger)
    {
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<ModDispatchResult> DispatchModPayloadAsync(
        string modSourcePath,
        string instancePath,
        EngineInfo? engineInfo = null,
        string? modName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modSourcePath) || !Directory.Exists(modSourcePath))
        {
            return Task.FromResult(new ModDispatchResult
            {
                Success = false,
                ErrorMessage = $"Mod source directory not found: '{modSourcePath}'"
            });
        }

        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return Task.FromResult(new ModDispatchResult
            {
                Success = false,
                ErrorMessage = $"Instance path not found: '{instancePath}'"
            });
        }

        try
        {
            var files = Directory.GetFiles(modSourcePath, "*.*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                return Task.FromResult(new ModDispatchResult
                {
                    Success = false,
                    ErrorMessage = "Mod source directory contains no files."
                });
            }

            var safeModName = PathHelper.SanitizeFolderName(modName ?? Path.GetFileName(modSourcePath));
            string targetCategory;
            string destDir;

            // 1. Check for Source Engine (.vpk)
            var vpkFiles = files.Where(f => f.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)).ToList();
            if (vpkFiles.Count > 0 || engineInfo?.Type is EngineType.Source or EngineType.Source2)
            {
                targetCategory = "SourceEngineAddons";
                var addonDirs = Directory.GetDirectories(instancePath, "addons", SearchOption.AllDirectories);
                if (addonDirs.Length > 0)
                {
                    destDir = Path.Combine(addonDirs[0], "workshop");
                }
                else
                {
                    destDir = Path.Combine(instancePath, "addons", "workshop");
                }
                Directory.CreateDirectory(destDir);
            }
            // 2. Check for Unreal Engine (.pak, .ucas, .utoc)
            else if (files.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) ||
                     engineInfo?.Type == EngineType.UnrealEngine)
            {
                targetCategory = "UnrealPaks";
                var paksDirs = Directory.GetDirectories(instancePath, "Paks", SearchOption.AllDirectories);
                if (paksDirs.Length > 0)
                {
                    destDir = Path.Combine(paksDirs[0], "~mods");
                }
                else
                {
                    destDir = Path.Combine(instancePath, "Content", "Paks", "~mods");
                }
                Directory.CreateDirectory(destDir);
            }
            // 3. Check for Unity C# assemblies (.dll)
            else if (files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) &&
                     (engineInfo?.Type == EngineType.Unity || Directory.Exists(Path.Combine(instancePath, "BepInEx"))))
            {
                targetCategory = "UnityBepInExPlugins";
                destDir = Path.Combine(instancePath, "BepInEx", "plugins", safeModName);
                Directory.CreateDirectory(destDir);
            }
            // 4. Check for Tabletop Simulator (.json with save/object data)
            else if (files.Any(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) &&
                     (instancePath.Contains("Tabletop Simulator", StringComparison.OrdinalIgnoreCase) || engineInfo?.Id == "tts"))
            {
                targetCategory = "TabletopSimulator";
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                destDir = Path.Combine(docs, "My Games", "Tabletop Simulator", "Mods", "Workshop");
                Directory.CreateDirectory(destDir);
            }
            // 5. Fallback Generic Mods folder
            else
            {
                targetCategory = "GenericMods";
                destDir = Path.Combine(instancePath, "mods", safeModName);
                Directory.CreateDirectory(destDir);
            }

            int dispatched = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(modSourcePath, file);
                var targetFile = Path.Combine(destDir, rel);
                var targetParent = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetParent)) Directory.CreateDirectory(targetParent);

                if (!_linker.CreateHardLink(targetFile, file))
                {
                    File.Copy(file, targetFile, overwrite: true);
                }
                dispatched++;
            }

            _logger.LogInformation("HeuristicModDispatcher dispatched {Count} files to '{Dest}' (Category={Category})",
                dispatched, destDir, targetCategory);

            return Task.FromResult(new ModDispatchResult
            {
                Success = true,
                DestinationPath = destDir,
                TargetCategory = targetCategory,
                FilesDispatched = dispatched
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch mod payload from '{Source}' to '{Instance}'", modSourcePath, instancePath);
            return Task.FromResult(new ModDispatchResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }
}
