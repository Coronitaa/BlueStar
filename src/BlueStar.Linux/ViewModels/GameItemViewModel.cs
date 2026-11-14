using System.Collections.ObjectModel;
using System.Diagnostics;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.Linux.ViewModels;

/// <summary>One library row: launch/stop and the ReFix option of an instance.</summary>
public sealed partial class GameItemViewModel : ObservableObject
{
    private readonly IGameLauncher _launcher;
    private readonly ReFixInstanceService _refix;
    private readonly IEmulatorRatingService _options;
    private readonly Action<string> _status;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Subtitle), nameof(ReFixText), nameof(HasReFix), nameof(Name), nameof(HasExe))]
    private GameInstance _instance;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Subtitle))] [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(InstallReFixCommand), nameof(UninstallReFixCommand))]
    private bool _isBusy;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(InstallReFixCommand))]
    private EmulatorOptionInfo? _selectedOption;

    public GameItemViewModel(GameInstance instance, IGameLauncher launcher, ReFixInstanceService refix, IEmulatorRatingService options, Action<string> status)
    {
        _instance = instance;
        _launcher = launcher;
        _refix = refix;
        _options = options;
        _status = status;
        _isRunning = launcher.IsRunning(instance.Id);
    }

    public ObservableCollection<EmulatorOptionInfo> Options { get; } = [];

    public string Name => Instance.Name;
    public bool HasExe => File.Exists(Instance.ExecutablePath);
    public bool HasReFix => Instance.EmulatorEnabled;
    public string ReFixText => Instance.EmulatorEnabled ? $"ReFix: {ReFixEmulator.GetOptionDisplayName(Instance.EmulatorId)} {Instance.InstalledEmulatorVersion}" : "";
    public string Subtitle => (IsRunning ? L.Running : HasExe ? L.Ready : L.NoExe) + " · " + Instance.InstallPath;

    public async Task LoadOptionsAsync()
    {
        try
        {
            var options = await _options.GetOptionsForInstanceAsync(Instance);
            Options.Clear();
            foreach (var o in options) Options.Add(o);
            SelectedOption = Options.FirstOrDefault(o => o.Id == Instance.EmulatorId) ?? Options.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _status($"{Name}: {ex.Message}");
        }
    }

    private bool CanPlay() => !IsRunning && !IsBusy && HasExe;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _launcher.LaunchAsync(Instance, line => _status(line));
            _status(result.Success ? $"{Name}: {L.Running}" : $"{Name}: {result.Message}");
            IsRunning = _launcher.IsRunning(Instance.Id);
        }
        finally { IsBusy = false; }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync() => await _launcher.KillAsync(Instance.Id);

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(Instance.InstallPath))
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Instance.InstallPath }, UseShellExecute = false });
    }

    private bool CanInstallReFix() => !IsBusy && SelectedOption != null;

    [RelayCommand(CanExecute = nameof(CanInstallReFix))]
    private async Task InstallReFixAsync()
    {
        var option = SelectedOption!;
        await RunBusyAsync(async progress =>
        {
            var updated = await _refix.InstallAsync(Instance, option.Id, progress);
            if (updated == null)
            {
                _status($"{Name}: ReFix ✗ (log: ~/.config/BlueStar/logs)");
                return;
            }
            Instance = updated;
            _status($"{Name}: {option.Name} ✓");
        });
    }

    private bool CanUninstallReFix() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUninstallReFix))]
    private Task UninstallReFixAsync() => RunBusyAsync(async progress =>
    {
        Instance = await _refix.UninstallAsync(Instance, progress);
        _status($"{Name}: ReFix ✓");
    });

    private async Task RunBusyAsync(Func<IProgress<DeployProgress>, Task> work)
    {
        IsBusy = true;
        try { await work(new Progress<DeployProgress>(p => _status($"{Name}: {p.Message}"))); }
        catch (Exception ex) { _status($"{Name}: {ex.Message}"); }
        finally { IsBusy = false; }
    }
}
