using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for the instance library view with search, engine/status filters, and rich Add Instance / ZIP Preview modal.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly IInstanceManager _instanceManager;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly IEngineDetector _engineDetector;
    private readonly IGameLauncher _gameLauncher;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    [ObservableProperty]
    private ObservableCollection<GameInstance> _instances = [];

    [ObservableProperty]
    private ObservableCollection<GameInstance> _filteredInstances = [];

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _selectedEngineFilter = "All";

    [ObservableProperty]
    private string _selectedStatusFilter = "All";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    // ── Add Instance & ZIP Preview Modal State ──
    [ObservableProperty]
    private bool _isAddMenuOpen;

    [ObservableProperty]
    private bool _isZipPreviewOpen;

    [ObservableProperty]
    private string? _pendingZipPath;

    [ObservableProperty]
    private string _previewGameName = string.Empty;

    [ObservableProperty]
    private uint _previewAppId;

    [ObservableProperty]
    private EngineInfo? _previewEngine;

    [ObservableProperty]
    private int _previewDepotsCount;

    [ObservableProperty]
    private int _previewDlcsCount;

    [ObservableProperty]
    private string _previewInstallPath = string.Empty;

    [ObservableProperty]
    private string _previewHeaderImageUrl = string.Empty;

    private DepotBoxArchive? _pendingArchive;

    public Action<GameInstance>? OnManageInstanceRequested { get; set; }

    public LibraryViewModel(
        IInstanceManager instanceManager,
        IDepotBoxArchiveParser archiveParser,
        IEngineDetector engineDetector,
        IGameLauncher gameLauncher,
        ILogger<LibraryViewModel> logger,
        IMetadataProvider? metadataProvider = null)
    {
        _instanceManager = instanceManager;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _gameLauncher = gameLauncher;
        _logger = logger;
        _metadataProvider = metadataProvider;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _gameLauncher.RunningStateChanged += OnRunningStateChanged;

        _ = LoadInstancesAsync();
    }

    private void OnRunningStateChanged(object? sender, (Guid InstanceId, bool IsRunning) e)
    {
        _uiContext.Post(_ =>
        {
            var inst = Instances.FirstOrDefault(i => i.Id == e.InstanceId);
            if (inst != null)
            {
                var updated = inst with { Status = e.IsRunning ? InstanceStatus.Running : InstanceStatus.Ready };
                var idx = Instances.IndexOf(inst);
                if (idx >= 0) Instances[idx] = updated;
                ApplyFilters();
            }
        }, null);
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchFilter = string.Empty;
    }

    partial void OnSearchFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedEngineFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        var rawSearch = (SearchFilter ?? string.Empty).Trim();
        var query = Instances.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(rawSearch))
        {
            query = query.Where(i =>
                (!string.IsNullOrEmpty(i.Name) && i.Name.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                i.AppId.ToString().Contains(rawSearch, StringComparison.OrdinalIgnoreCase) ||
                (i.Engine != null && !string.IsNullOrEmpty(i.Engine.Name) && i.Engine.Name.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Engine != null && !string.IsNullOrEmpty(i.Engine.DisplayText) && i.Engine.DisplayText.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Metadata != null && !string.IsNullOrEmpty(i.Metadata.Developer) && i.Metadata.Developer.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Metadata != null && !string.IsNullOrEmpty(i.Metadata.Publisher) && i.Metadata.Publisher.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(i.InstallPath) && i.InstallPath.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)));
        }

        if (SelectedEngineFilter != "All")
        {
            query = SelectedEngineFilter switch
            {
                "Unreal" => query.Where(i => i.Engine?.Type == EngineType.UnrealEngine),
                "Unity" => query.Where(i => i.Engine?.Type == EngineType.Unity),
                "Godot" => query.Where(i => i.Engine?.Type == EngineType.Godot),
                "Source" => query.Where(i => i.Engine?.Type is EngineType.Source or EngineType.Source2),
                "Other" => query.Where(i => i.Engine?.Type is not (EngineType.UnrealEngine or EngineType.Unity or EngineType.Godot or EngineType.Source or EngineType.Source2)),
                _ => query
            };
        }

        if (SelectedStatusFilter != "All")
        {
            query = SelectedStatusFilter switch
            {
                "Ready" => query.Where(i => i.Status is InstanceStatus.Ready or InstanceStatus.Running),
                "Running" => query.Where(i => i.Status == InstanceStatus.Running),
                "Downloading" => query.Where(i => i.Status == InstanceStatus.Downloading),
                "NotInstalled" => query.Where(i => i.Status == InstanceStatus.NotInstalled),
                _ => query
            };
        }

        FilteredInstances = new ObservableCollection<GameInstance>(query.ToList());
    }

    [RelayCommand]
    public async Task LoadInstancesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var all = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var sanitized = all.Select(i => i with
            {
                Name = CleanName(i.Name) ?? i.Name,
                Dlcs = i.Dlcs.Select(d => d with { Name = CleanName(d.Name) ?? d.Name }).ToList().AsReadOnly()
            }).ToList();

            // Display instances immediately on UI thread without blocking
            Instances = new ObservableCollection<GameInstance>(sanitized);
            ApplyFilters();
            _logger.LogInformation("Loaded {Count} instances", Instances.Count);

            // Perform engine detection and metadata enrichment in background
            _ = Task.Run(async () =>
            {
                foreach (var inst in sanitized)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(inst.InstallPath) && Directory.Exists(inst.InstallPath))
                        {
                            var engine = await _engineDetector.DetectEngineAsync(inst.InstallPath, CancellationToken.None).ConfigureAwait(false);

                            if (engine != null && engine.Type != EngineType.Generic && (inst.Engine == null || inst.Engine.Type == EngineType.Generic))
                            {
                                var updated = inst with { Engine = engine };
                                await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);

                                _uiContext.Post(_ =>
                                {
                                    var existing = Instances.FirstOrDefault(x => x.Id == inst.Id);
                                    if (existing != null)
                                    {
                                        var idx = Instances.IndexOf(existing);
                                        if (idx >= 0)
                                        {
                                            Instances[idx] = updated;
                                            ApplyFilters();
                                        }
                                    }
                                }, null);
                            }
                        }
                    }
                    catch { }
                }

                await FetchMissingMetadataAsync(sanitized).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load instances");
            ErrorMessage = $"Failed to load instances: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleAddMenu() => IsAddMenuOpen = !IsAddMenuOpen;

    [RelayCommand]
    private void CloseAddMenu() => IsAddMenuOpen = false;

    [RelayCommand]
    public void BrowsePreviewInstallPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Installation Directory",
            InitialDirectory = Directory.Exists(PreviewInstallPath) ? PreviewInstallPath : null
        };

        if (dialog.ShowDialog() == true)
        {
            PreviewInstallPath = dialog.FolderName;
        }
    }

    /// <summary>
    /// Step 1: User selects ZIP. We parse it and display the Preview Modal.
    /// </summary>
    [RelayCommand]
    public async Task StartImportZipAsync()
    {
        IsAddMenuOpen = false;

        var dialog = new OpenFileDialog
        {
            Title = "Import DepotBox Archive",
            Filter = "ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var zipPath = dialog.FileName;
            _logger.LogInformation("Parsing DepotBox archive for preview: {Path}", zipPath);

            var archive = await _archiveParser.ParseAsync(zipPath, CancellationToken.None).ConfigureAwait(true);
            if (archive.Games.Count == 0)
            {
                ErrorMessage = "No games found inside the selected ZIP archive.";
                return;
            }

            var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
            var cleanMainName = CleanName(mainGame.Name) ?? $"App {mainGame.AppId}";
            var baseDir = Path.GetDirectoryName(zipPath) ?? string.Empty;
            var installPath = PathHelper.EnsureGameSubfolder(baseDir, cleanMainName);

            // Detect engine from proposed install path or default
            var engine = await _engineDetector.DetectEngineAsync(installPath, CancellationToken.None).ConfigureAwait(true);

            _pendingArchive = archive;
            PendingZipPath = zipPath;
            PreviewGameName = cleanMainName;
            PreviewAppId = mainGame.AppId;
            PreviewEngine = engine;
            PreviewDepotsCount = archive.Games.Sum(g => g.Depots.Count);
            PreviewDlcsCount = archive.Games.Count(g => g.IsDlc);
            PreviewInstallPath = installPath;
            PreviewHeaderImageUrl = await ResolveBannerUrlAsync(mainGame.AppId).ConfigureAwait(true);

            // Open Modal
            IsZipPreviewOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect ZIP archive");
            ErrorMessage = $"Failed to read ZIP: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static async Task<string> ResolveBannerUrlAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return string.Empty;

        var urls = new[]
        {
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg"
        };

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var url in urls)
        {
            try
            {
                using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return url;
            }
            catch { }
        }

        return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
    }

    /// <summary>
    /// Step 2: User confirms ZIP import from the Preview Modal.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmZipImportAsync()
    {
        if (_pendingArchive == null || string.IsNullOrWhiteSpace(PendingZipPath))
        {
            IsZipPreviewOpen = false;
            return;
        }

        IsLoading = true;
        try
        {
            var instance = BuildInstanceFromArchive(_pendingArchive, PendingZipPath) with
            {
                Name = PreviewGameName,
                InstallPath = PreviewInstallPath,
                Engine = PreviewEngine
            };

            var created = await _instanceManager.CreateAsync(instance, CancellationToken.None).ConfigureAwait(true);
            ExtractManifestsToInstanceStorage(PendingZipPath, created.Id);

            Instances.Add(created);
            ApplyFilters();

            _logger.LogInformation("Successfully imported instance: {Name} ({AppId})", created.Name, created.AppId);
            _ = FetchMissingMetadataAsync(new List<GameInstance> { created });

            IsZipPreviewOpen = false;
            _pendingArchive = null;
            PendingZipPath = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm ZIP import");
            ErrorMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void CancelZipImport()
    {
        IsZipPreviewOpen = false;
        _pendingArchive = null;
        PendingZipPath = null;
    }

    /// <summary>
    /// Adds an existing game install folder.
    /// </summary>
    [RelayCommand]
    public async Task AddExistingFolderAsync()
    {
        IsAddMenuOpen = false;

        var dialog = new OpenFolderDialog
        {
            Title = "Select Existing Game Installation Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (!Directory.Exists(folder)) return;

        IsLoading = true;
        try
        {
            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var engine = await _engineDetector.DetectEngineAsync(folder, CancellationToken.None).ConfigureAwait(true);
            var exe = _engineDetector.FindPrimaryExecutable(folder, folderName);

            var newInstance = new GameInstance
            {
                Name = folderName,
                AppId = 0,
                InstallPath = folder,
                ExecutablePath = exe,
                Engine = engine,
                Status = File.Exists(exe) ? InstanceStatus.Ready : InstanceStatus.NotInstalled
            };

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            Instances.Add(created);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add game folder");
            ErrorMessage = $"Failed to add folder: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayInstanceAsync(GameInstance instance)
    {
        if (instance == null) return;
        _logger.LogInformation("Launching instance {Name}", instance.Name);
        var result = await _gameLauncher.LaunchAsync(instance, null, CancellationToken.None).ConfigureAwait(true);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void ManageInstance(GameInstance instance)
    {
        OnManageInstanceRequested?.Invoke(instance);
    }

    [RelayCommand]
    private void OpenFolder(GameInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            Process.Start(new ProcessStartInfo { FileName = instance.InstallPath, UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync(Guid id)
    {
        try
        {
            await _instanceManager.DeleteAsync(id, CancellationToken.None).ConfigureAwait(true);
            var toRemove = Instances.FirstOrDefault(i => i.Id == id);
            if (toRemove is not null)
            {
                Instances.Remove(toRemove);
                ApplyFilters();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete instance {Id}", id);
            ErrorMessage = $"Delete failed: {ex.Message}";
        }
    }

    private async Task FetchMissingMetadataAsync(List<GameInstance> list)
    {
        if (_metadataProvider is null) return;

        foreach (var instance in list)
        {
            if (instance.Metadata is not null || instance.AppId == 0) continue;

            try
            {
                var meta = await _metadataProvider.GetMetadataAsync(instance.AppId, CancellationToken.None).ConfigureAwait(false);
                if (meta is not null)
                {
                    var updated = instance with { Metadata = meta };
                    await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);

                    _uiContext.Post(_ =>
                    {
                        var idx = Instances.IndexOf(instance);
                        if (idx >= 0)
                        {
                            Instances[idx] = updated;
                            ApplyFilters();
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch Steam metadata for AppId={AppId}", instance.AppId);
            }
        }
    }

    private static GameInstance BuildInstanceFromArchive(DepotBoxArchive archive, string zipPath)
    {
        var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
        var cleanMainName = CleanName(mainGame.Name) ?? $"App {mainGame.AppId}";
        var baseDir = Path.GetDirectoryName(zipPath) ?? string.Empty;

        return new GameInstance
        {
            Name = cleanMainName,
            AppId = mainGame.AppId,
            InstallPath = PathHelper.EnsureGameSubfolder(baseDir, cleanMainName),
            SourceArchivePath = zipPath,
            Status = InstanceStatus.NotInstalled,
            Depots = archive.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
            {
                DepotId = d.DepotId,
                ManifestId = d.ManifestId,
                SizeBytes = d.SizeBytes,
                DepotKey = g.DepotKey,
                Name = d.Name ?? (g.IsDlc ? $"{CleanName(g.Name)} Depot" : "Base Game Content"),
                Category = d.Category,
                Platform = d.Platform,
                Architecture = d.Architecture,
                IsSharedDepot = false
            })).DistinctBy(d => d.DepotId).ToList().AsReadOnly(),
            Dlcs = archive.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
            {
                AppId = dlc.AppId,
                Name = CleanName(dlc.Name) ?? $"DLC {dlc.AppId}",
                Category = "DLC",
                Platform = dlc.Depots.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Platform))?.Platform ?? "Universal",
                Depots = dlc.Depots.Select(d => new DepotInfo
                {
                    DepotId = d.DepotId,
                    ManifestId = d.ManifestId,
                    SizeBytes = d.SizeBytes,
                    DepotKey = dlc.DepotKey,
                    Name = d.Name ?? $"{CleanName(dlc.Name)} Depot",
                    Category = "DLC",
                    Platform = d.Platform,
                    Architecture = d.Architecture,
                    IsSharedDepot = false
                }).ToList().AsReadOnly(),
                IsInstalled = false
            }).ToList().AsReadOnly()
        };
    }

    private static void ExtractManifestsToInstanceStorage(string zipPath, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return;

        try
        {
            var manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", instanceId.ToString(), "manifests");
            Directory.CreateDirectory(manifestDir);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;

                var dest = Path.Combine(manifestDir, entry.Name);
                using var entryStream = entry.Open();
                using var fileStream = File.Create(dest);
                entryStream.CopyTo(fileStream);
            }
        }
        catch { }
    }

    private static string? CleanName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return rawName;
        var name = rawName.Trim();
        var prefixes = new[] { "Gamename ", "Gamename", "Dlcname ", "Dlcname", "Game Name:", "Game Name ", "Game:", "Name:", "App:" };
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                name = name[p.Length..].Trim();
        }
        return name;
    }
}
