using System.Collections.ObjectModel;
using Avalonia.Threading;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.Linux.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IInstanceManager _instances;
    private readonly IGameLauncher _launcher;
    private readonly GameImportService _import;
    private readonly ReFixInstanceService _refix;
    private readonly IEmulatorRatingService _options;

    [ObservableProperty] private string _status = L.Beta;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private int _count;
    [ObservableProperty] private bool _isBusy;

    public LibraryViewModel(IInstanceManager instances, IGameLauncher launcher, GameImportService import, ReFixInstanceService refix, IEmulatorRatingService options)
    {
        _instances = instances;
        _launcher = launcher;
        _import = import;
        _refix = refix;
        _options = options;
        _launcher.RunningStateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            var game = Games.FirstOrDefault(g => g.Instance.Id == e.InstanceId);
            if (game != null) game.IsRunning = e.IsRunning;
        });
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ObservableCollection<InstalledSteamGame> SteamGames { get; } = [];
    public bool IsEmpty => Count == 0;

    /// <summary>Set by the view: opens the folder picker, returns the chosen path or null.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var all = await _instances.GetAllAsync();
        Games.Clear();
        foreach (var instance in all.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(NewItem(instance));
        Count = Games.Count;
        await Task.WhenAll(Games.Select(g => g.LoadOptionsAsync())); // one rating request per game: run them together
    }

    /// <summary>Forgets the game; its files stay where they are (re-import the folder to bring it back).</summary>
    [RelayCommand]
    private async Task RemoveAsync(GameItemViewModel game)
    {
        if (game.IsRunning) return;
        await _instances.DeleteAsync(game.Instance.Id);
        Games.Remove(game);
        Count = Games.Count;
        Status = $"{game.Name}: {L.Removed}";
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = PickFolder == null ? null : await PickFolder();
        if (folder != null)
            await ImportAsync(() => _import.ImportFolderAsync(folder));
    }

    [RelayCommand]
    private async Task ScanSteamAsync()
    {
        IsBusy = true;
        try
        {
            var imported = (await _instances.GetAllAsync()).Select(i => i.InstallPath).ToHashSet();
            SteamGames.Clear();
            foreach (var game in await SteamLibraryScanner.ScanInstalledGamesAsync())
                if (!imported.Contains(game.FullPath))
                    SteamGames.Add(game);
            Status = $"Steam: {SteamGames.Count}";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ImportSteamAsync(InstalledSteamGame game)
    {
        await ImportAsync(() => _import.ImportSteamGameAsync(game));
        SteamGames.Remove(game);
    }

    private async Task ImportAsync(Func<Task<GameInstance>> import)
    {
        IsBusy = true;
        try
        {
            var created = await import();
            var item = NewItem(created);
            Games.Add(item);
            Count = Games.Count;
            await item.LoadOptionsAsync();
            Status = $"{created.Name} ✓";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private GameItemViewModel NewItem(GameInstance instance) =>
        new(instance, _launcher, _refix, _options, text => Dispatcher.UIThread.Post(() => Status = text));
}
