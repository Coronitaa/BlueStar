using System;
using System.ComponentModel;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// State and coordinator for the unified Mod Explorer modal.
/// Organizes mod providers (Thunderstore, Steam Workshop, etc.) as top-level category tabs
/// inspired by BlueStar's Explore catalog, showing strictly the providers supported by the game.
/// </summary>
public sealed partial class ModBrowserViewModel : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;
    private GameInstance? _instance;
    private string? _thunderstoreCommunity;

    public ModBrowserViewModel(
        WorkshopBrowserViewModel workshop,
        ThunderstoreBrowserViewModel? thunderstore,
        ILocalizationService? localization,
        ILogger logger)
    {
        Workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        Thunderstore = thunderstore;
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (Thunderstore != null)
        {
            Thunderstore.OnCloseRequested = Close;
            Thunderstore.PropertyChanged += OnThunderstorePropertyChanged;
        }
    }

    public WorkshopBrowserViewModel Workshop { get; }
    public ThunderstoreBrowserViewModel? Thunderstore { get; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _selectedProvider = "Thunderstore";

    [ObservableProperty]
    private string? _instanceName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProviderSupport))]
    private bool _hasWorkshopSupport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProviderSupport))]
    private bool _hasThunderstoreSupport;

    public bool HasAnyProviderSupport => HasWorkshopSupport || HasThunderstoreSupport;

    public string TotalResultsText
    {
        get
        {
            if (SelectedProvider == "Thunderstore" && Thunderstore != null)
            {
                if (Thunderstore.TotalPackagesCount > 0)
                {
                    return string.Format(T("String_ModBrowserTotalMods", "{0:N0} mods disponibles"), Thunderstore.TotalPackagesCount);
                }
            }
            return string.Empty;
        }
    }

    public void Open(
        GameInstance instance,
        bool hasWorkshop,
        bool hasThunderstore,
        string? thunderstoreCommunity = null,
        string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _instance = instance;
        _thunderstoreCommunity = thunderstoreCommunity;
        InstanceName = instance.Name;
        HasWorkshopSupport = hasWorkshop;
        HasThunderstoreSupport = hasThunderstore && !string.IsNullOrWhiteSpace(thunderstoreCommunity);

        // Pick preferred or first available provider
        if (!string.IsNullOrWhiteSpace(preferredProvider) &&
            ((preferredProvider.Equals("Workshop", StringComparison.OrdinalIgnoreCase) && HasWorkshopSupport) ||
             (preferredProvider.Equals("Thunderstore", StringComparison.OrdinalIgnoreCase) && HasThunderstoreSupport)))
        {
            SelectedProvider = preferredProvider.Equals("Workshop", StringComparison.OrdinalIgnoreCase) ? "Workshop" : "Thunderstore";
        }
        else if (HasThunderstoreSupport)
        {
            SelectedProvider = "Thunderstore";
        }
        else if (HasWorkshopSupport)
        {
            SelectedProvider = "Workshop";
        }
        else
        {
            SelectedProvider = "Thunderstore";
        }

        IsOpen = true;

        // Initialize sub-viewmodels
        if (HasWorkshopSupport)
        {
            Workshop.Open(instance.AppId, HasThunderstoreSupport);
            Workshop.IsOpen = true;
        }

        if (HasThunderstoreSupport && !string.IsNullOrWhiteSpace(thunderstoreCommunity))
        {
            Thunderstore?.Open(instance, thunderstoreCommunity, HasWorkshopSupport);
        }

        OnPropertyChanged(nameof(TotalResultsText));
    }

    [RelayCommand]
    public void SelectProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;

        if (provider.Equals("Workshop", StringComparison.OrdinalIgnoreCase) && HasWorkshopSupport)
        {
            SelectedProvider = "Workshop";
            if (_instance != null && !Workshop.IsOpen)
            {
                Workshop.Open(_instance.AppId, HasThunderstoreSupport);
                Workshop.IsOpen = true;
            }
        }
        else if (provider.Equals("Thunderstore", StringComparison.OrdinalIgnoreCase) && HasThunderstoreSupport)
        {
            SelectedProvider = "Thunderstore";
            if (Thunderstore != null && Thunderstore.Packages.Count == 0 && !Thunderstore.IsLoading &&
                _instance != null && !string.IsNullOrWhiteSpace(_thunderstoreCommunity))
            {
                Thunderstore.Open(_instance, _thunderstoreCommunity, HasWorkshopSupport);
            }
        }

        OnPropertyChanged(nameof(TotalResultsText));
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Workshop.Close();
        Thunderstore?.Close();
    }

    private void OnThunderstorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThunderstoreBrowserViewModel.TotalPackagesCount) ||
            e.PropertyName == nameof(ThunderstoreBrowserViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(TotalResultsText));
        }
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
}
