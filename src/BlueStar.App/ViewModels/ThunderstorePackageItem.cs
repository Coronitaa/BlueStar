using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Display item in the Thunderstore Content Browser modal.
/// </summary>
public sealed partial class ThunderstorePackageItem : ObservableObject
{
    private readonly Func<ThunderstorePackageItem, Task> _installAction;

    public ThunderstorePackage Package { get; }

    public string Name => Package.Name;
    public string Owner => Package.Owner;
    public string FullName => Package.FullName;
    public string Description => Package.LatestVersion?.Description ?? string.Empty;
    public string? IconUrl => Package.LatestVersion?.IconUrl;
    public IReadOnlyList<string> Categories => Package.Categories;
    public long TotalDownloads => Package.TotalDownloads;
    public int RatingScore => Package.RatingScore;
    public DateTimeOffset DateUpdated => Package.DateUpdated;

    public string FormattedDownloads => FormatCount(Package.TotalDownloads);
    public string FormattedDate => Package.DateUpdated != DateTimeOffset.MinValue
        ? Package.DateUpdated.ToString("yyyy-MM-dd")
        : string.Empty;

    public ObservableCollection<ThunderstorePackageVersion> AvailableVersions { get; } = [];

    [ObservableProperty]
    private ThunderstorePackageVersion? _selectedVersion;

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
            if (SelectedVersion != null && !string.IsNullOrWhiteSpace(InstalledVersion) &&
                !string.Equals(SelectedVersion.VersionNumber, InstalledVersion, StringComparison.OrdinalIgnoreCase))
            {
                return $"Switch to v{SelectedVersion.VersionNumber}";
            }
            return "Installed";
        }
    }

    public ThunderstorePackageItem(ThunderstorePackage package, Func<ThunderstorePackageItem, Task> installAction)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        _installAction = installAction ?? throw new ArgumentNullException(nameof(installAction));

        foreach (var v in package.Versions)
        {
            AvailableVersions.Add(v);
        }
        SelectedVersion = package.LatestVersion ?? AvailableVersions.FirstOrDefault();
    }

    partial void OnSelectedVersionChanged(ThunderstorePackageVersion? value)
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
