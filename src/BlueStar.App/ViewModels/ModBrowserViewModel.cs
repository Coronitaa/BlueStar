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
/// Organizes mod providers (Thunderstore, Steam Workshop, CurseForge, mod.io, Nexus Mods)
/// as top-level category tabs, showing strictly the providers supported by the game.
/// </summary>
public sealed partial class ModBrowserViewModel : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;
    private GameInstance? _instance;
    private string? _thunderstoreCommunity;
    private int? _curseForgeGameId;
    private string? _nexusGameDomain;

    public ModBrowserViewModel(
        WorkshopBrowserViewModel workshop,
        ThunderstoreBrowserViewModel? thunderstore,
        ILocalizationService? localization,
        ILogger logger,
        CurseForgeBrowserViewModel? curseForge = null,
        NexusBrowserViewModel? nexus = null)
    {
        Workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        Thunderstore = thunderstore;
        CurseForge = curseForge;
        Nexus = nexus;
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Workshop.PropertyChanged += OnProviderPropertyChanged;

        if (Thunderstore != null)
        {
            Thunderstore.OnCloseRequested = Close;
            Thunderstore.OpenModPageAction = (title, url) => OpenModPageAction?.Invoke(title, url);
            Thunderstore.PropertyChanged += OnProviderPropertyChanged;
        }
        if (CurseForge != null)
        {
            CurseForge.PropertyChanged += OnProviderPropertyChanged;
        }
        if (Nexus != null)
        {
            Nexus.PropertyChanged += OnProviderPropertyChanged;
        }
    }

    /// <summary>Notification callback when clicking on a package or mod to view its web page.</summary>
    public Action<string, string>? OpenModPageAction { get; set; }

    public WorkshopBrowserViewModel Workshop { get; }
    public ThunderstoreBrowserViewModel? Thunderstore { get; }
    public CurseForgeBrowserViewModel? CurseForge { get; }
    public NexusBrowserViewModel? Nexus { get; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProviderSupport))]
    private bool _hasCurseForgeSupport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProviderSupport))]
    private bool _hasNexusSupport;

    public bool HasAnyProviderSupport => HasWorkshopSupport || HasThunderstoreSupport || HasCurseForgeSupport || HasNexusSupport;

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
            else if (SelectedProvider == "CurseForge" && CurseForge != null)
            {
                if (CurseForge.TotalCount > 0)
                {
                    return string.Format(T("String_ModBrowserTotalMods", "{0:N0} mods disponibles"), CurseForge.TotalCount);
                }
            }
            return string.Empty;
        }
    }

    public void UpdateProviderInfo(
        bool hasWorkshop,
        bool hasThunderstore,
        string? thunderstoreCommunity = null,
        bool hasCurseForge = false,
        int? curseForgeGameId = null,
        bool hasNexus = false,
        string? nexusGameDomain = null)
    {
        if (!string.IsNullOrWhiteSpace(thunderstoreCommunity))
        {
            _thunderstoreCommunity = thunderstoreCommunity;
        }
        if (curseForgeGameId.HasValue && curseForgeGameId.Value > 0)
        {
            _curseForgeGameId = curseForgeGameId;
        }
        if (!string.IsNullOrWhiteSpace(nexusGameDomain))
        {
            _nexusGameDomain = nexusGameDomain;
        }

        HasWorkshopSupport = hasWorkshop;
        HasThunderstoreSupport = hasThunderstore && !string.IsNullOrWhiteSpace(_thunderstoreCommunity);
        HasCurseForgeSupport = hasCurseForge && _curseForgeGameId.HasValue && _curseForgeGameId.Value > 0;
        HasNexusSupport = hasNexus && !string.IsNullOrWhiteSpace(_nexusGameDomain);

        // If the current provider is no longer available, switch to first available
        if (!IsProviderAvailable(SelectedProvider))
        {
            if (HasThunderstoreSupport) SelectedProvider = "Thunderstore";
            else if (HasWorkshopSupport) SelectedProvider = "Workshop";
            else if (HasCurseForgeSupport) SelectedProvider = "CurseForge";
            else if (HasNexusSupport) SelectedProvider = "Nexus";
        }

        if (IsOpen && _instance != null)
        {
            if (SelectedProvider == "CurseForge" && HasCurseForgeSupport && _curseForgeGameId.HasValue && CurseForge != null && (CurseForge.GameId != _curseForgeGameId.Value || CurseForge.Mods.Count == 0) && !CurseForge.IsLoading)
            {
                CurseForge.Open(_instance, _curseForgeGameId.Value);
            }
            else if (SelectedProvider == "Nexus" && HasNexusSupport && !string.IsNullOrWhiteSpace(_nexusGameDomain) && Nexus != null && string.IsNullOrWhiteSpace(Nexus.GameDomain))
            {
                Nexus.Open(_instance, _nexusGameDomain);
            }
        }
    }

    public void Open(
        GameInstance instance,
        bool hasWorkshop,
        bool hasThunderstore,
        string? thunderstoreCommunity = null,
        bool hasCurseForge = false,
        int? curseForgeGameId = null,
        bool hasNexus = false,
        string? nexusGameDomain = null,
        string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _instance = instance;
        InstanceName = instance.Name;

        UpdateProviderInfo(
            hasWorkshop,
            hasThunderstore,
            thunderstoreCommunity,
            hasCurseForge,
            curseForgeGameId,
            hasNexus,
            nexusGameDomain);

        // Pick preferred or first available provider
        if (!string.IsNullOrWhiteSpace(preferredProvider) && IsProviderAvailable(preferredProvider))
        {
            SelectedProvider = preferredProvider;
        }

        IsOpen = true;

        // Initialize sub-viewmodels
        if (HasWorkshopSupport)
        {
            Workshop.Open(instance.AppId, HasThunderstoreSupport);
            Workshop.IsOpen = true;
        }

        if (HasThunderstoreSupport && !string.IsNullOrWhiteSpace(_thunderstoreCommunity))
        {
            Thunderstore?.Open(instance, _thunderstoreCommunity, HasWorkshopSupport);
        }

        if (HasCurseForgeSupport && _curseForgeGameId.HasValue)
        {
            CurseForge?.Open(instance, _curseForgeGameId.Value);
        }

        if (HasNexusSupport && !string.IsNullOrWhiteSpace(_nexusGameDomain))
        {
            Nexus?.Open(instance, _nexusGameDomain);
        }

        OnPropertyChanged(nameof(TotalResultsText));
    }

    private bool IsProviderAvailable(string provider) => provider.ToLowerInvariant() switch
    {
        "thunderstore" => HasThunderstoreSupport,
        "workshop" => HasWorkshopSupport,
        "curseforge" => HasCurseForgeSupport,
        "nexus" => HasNexusSupport,
        _ => false
    };

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
        else if (provider.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) && HasCurseForgeSupport)
        {
            SelectedProvider = "CurseForge";
            if (CurseForge != null && (CurseForge.GameId != _curseForgeGameId || CurseForge.Mods.Count == 0) && !CurseForge.IsLoading &&
                _instance != null && _curseForgeGameId.HasValue)
            {
                CurseForge.Open(_instance, _curseForgeGameId.Value);
            }
        }
        else if (provider.Equals("Nexus", StringComparison.OrdinalIgnoreCase) && HasNexusSupport)
        {
            SelectedProvider = "Nexus";
            if (Nexus != null && _instance != null && !string.IsNullOrWhiteSpace(_nexusGameDomain))
            {
                Nexus.Open(_instance, _nexusGameDomain);
            }
        }

        OnPropertyChanged(nameof(TotalResultsText));
        NotifyActiveStatusChanged();
    }

    partial void OnSelectedProviderChanged(string value)
    {
        NotifyActiveStatusChanged();
    }

    public string? ActiveStatusMessage => SelectedProvider switch
    {
        "Thunderstore" => Thunderstore?.StatusMessage,
        "CurseForge" => CurseForge?.StatusMessage,
        "Nexus" => Nexus?.StatusMessage,
        "Workshop" => Workshop?.StatusMessage,
        _ => null
    };

    public bool ActiveIsStatusError => SelectedProvider switch
    {
        "Thunderstore" => Thunderstore?.IsStatusError ?? false,
        "CurseForge" => CurseForge?.IsStatusError ?? false,
        "Nexus" => Nexus?.IsStatusError ?? false,
        "Workshop" => Workshop?.IsStatusError ?? false,
        _ => false
    };

    public bool ActiveIsStatusSuccess
    {
        get
        {
            var msg = ActiveStatusMessage;
            if (string.IsNullOrWhiteSpace(msg) || ActiveIsStatusError) return false;
            return msg.Contains("✅") ||
                   msg.Contains("instalado", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("éxito", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("completad", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool ActiveIsStatusProgress
    {
        get
        {
            if (ActiveIsStatusError || ActiveIsStatusSuccess) return false;
            var isBusy = (SelectedProvider == "Thunderstore" && ((Thunderstore?.IsLoading ?? false) || (Thunderstore?.IsLoadingMore ?? false))) ||
                         (SelectedProvider == "Nexus" && (Nexus?.IsDownloading ?? false)) ||
                         (SelectedProvider == "CurseForge" && ((CurseForge?.IsLoading ?? false) || (CurseForge?.IsLoadingMore ?? false))) ||
                         (SelectedProvider == "Workshop" && (Workshop?.IsDownloading ?? false));
            var msg = ActiveStatusMessage;
            if (isBusy) return true;
            if (string.IsNullOrWhiteSpace(msg)) return false;
            return msg.Contains("⏳") ||
                   msg.Contains("Descargando", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Instalando", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Cargando", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Iniciando", StringComparison.OrdinalIgnoreCase);
        }
    }

    private CancellationTokenSource? _statusDismissCts;

    public void NotifyActiveStatusChanged()
    {
        OnPropertyChanged(nameof(ActiveStatusMessage));
        OnPropertyChanged(nameof(ActiveIsStatusError));
        OnPropertyChanged(nameof(ActiveIsStatusSuccess));
        OnPropertyChanged(nameof(ActiveIsStatusProgress));

        _statusDismissCts?.Cancel();
        _statusDismissCts?.Dispose();
        _statusDismissCts = null;

        var msg = ActiveStatusMessage;
        if (!string.IsNullOrWhiteSpace(msg) && !ActiveIsStatusProgress)
        {
            _statusDismissCts = new CancellationTokenSource();
            var ct = _statusDismissCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested)
                    {
                        App.Current?.Dispatcher?.InvokeAsync(ClearActiveStatus);
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }
    }

    public void ClearActiveStatus()
    {
        if (Thunderstore != null) Thunderstore.StatusMessage = null;
        if (CurseForge != null) CurseForge.StatusMessage = null;
        if (Nexus != null) Nexus.StatusMessage = null;
        if (Workshop != null) Workshop.StatusMessage = null;

        OnPropertyChanged(nameof(ActiveStatusMessage));
        OnPropertyChanged(nameof(ActiveIsStatusError));
        OnPropertyChanged(nameof(ActiveIsStatusSuccess));
        OnPropertyChanged(nameof(ActiveIsStatusProgress));
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _statusDismissCts?.Cancel();
        _statusDismissCts?.Dispose();
        _statusDismissCts = null;
        ClearActiveStatus();
        Workshop.Close();
        Thunderstore?.Close();
        CurseForge?.Close();
        Nexus?.Close();
    }

    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThunderstoreBrowserViewModel.TotalPackagesCount) ||
            e.PropertyName == nameof(ThunderstoreBrowserViewModel.IsLoading) ||
            e.PropertyName == nameof(CurseForgeBrowserViewModel.TotalCount) ||
            e.PropertyName == nameof(CurseForgeBrowserViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(TotalResultsText));
        }

        if (e.PropertyName is nameof(ThunderstoreBrowserViewModel.StatusMessage) or
                               nameof(ThunderstoreBrowserViewModel.IsStatusError) or
                               nameof(ThunderstoreBrowserViewModel.IsLoading) or
                               nameof(NexusBrowserViewModel.StatusMessage) or
                               nameof(NexusBrowserViewModel.IsStatusError) or
                               nameof(NexusBrowserViewModel.IsDownloading) or
                               nameof(CurseForgeBrowserViewModel.StatusMessage) or
                               nameof(CurseForgeBrowserViewModel.IsStatusError) or
                               nameof(CurseForgeBrowserViewModel.IsLoading) or
                               nameof(WorkshopBrowserViewModel.StatusMessage) or
                               nameof(WorkshopBrowserViewModel.IsStatusError) or
                               nameof(WorkshopBrowserViewModel.IsDownloading))
        {
            NotifyActiveStatusChanged();
        }
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
}
