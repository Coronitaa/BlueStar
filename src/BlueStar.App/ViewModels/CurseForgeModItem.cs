using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Display item in the CurseForge Mod Explorer tab.
/// </summary>
public sealed partial class CurseForgeModItem : ObservableObject
{
    private readonly Func<CurseForgeModItem, Task> _installAction;

    public CurseForgeMod Mod { get; }

    public int Id => Mod.Id;
    public string Name => Mod.Name;
    public string Summary => Mod.Summary;
    public string? Description => Mod.Description;
    public string? ThumbnailUrl => Mod.ThumbnailUrl;
    public string? PrimaryAuthor => Mod.PrimaryAuthor;
    public long DownloadCount => Mod.DownloadCount;
    public string? WebsiteUrl => Mod.WebsiteUrl;
    public System.Collections.Generic.IReadOnlyList<string> Categories => Mod.Categories;
    public DateTimeOffset DateModified => Mod.DateModified;

    public string FormattedDownloads => FormatCount(Mod.DownloadCount);
    public string FormattedDate => Mod.DateModified != DateTimeOffset.MinValue
        ? Mod.DateModified.ToString("yyyy-MM-dd")
        : string.Empty;

    public ObservableCollection<CurseForgeFile> AvailableFiles { get; } = [];

    [ObservableProperty]
    private CurseForgeFile? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    private string? _installedVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _downloadProgress;

    public bool CanInstall => !IsInstalling;

    public string ButtonLabel
    {
        get
        {
            if (!IsInstalled) return "Install";
            if (SelectedFile != null && !string.IsNullOrWhiteSpace(InstalledVersion) &&
                !string.Equals(SelectedFile.DisplayName, InstalledVersion, StringComparison.OrdinalIgnoreCase))
            {
                return $"Switch to {SelectedFile.DisplayName}";
            }
            return "Installed";
        }
    }

    public CurseForgeModItem(CurseForgeMod mod, Func<CurseForgeModItem, Task> installAction)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _installAction = installAction ?? throw new ArgumentNullException(nameof(installAction));

        foreach (var f in mod.Files)
        {
            AvailableFiles.Add(f);
        }
        SelectedFile = mod.LatestFile ?? AvailableFiles.FirstOrDefault();
    }

    partial void OnSelectedFileChanged(CurseForgeFile? value)
    {
        OnPropertyChanged(nameof(ButtonLabel));
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    public async Task InstallAsync()
    {
        await _installAction(this).ConfigureAwait(true);
    }

    private static string FormatCount(long count)
    {
        if (count >= 1_000_000) return (count / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
        if (count >= 1_000) return (count / 1_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
