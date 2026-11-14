using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Compat;
using BlueStar.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.Linux.ViewModels;

public sealed record ChoiceOption<T>(string Label, T Value);

/// <summary>Settings → "Compatibilidad (Wine/Proton)": global <see cref="CompatSettings"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;

    [ObservableProperty] private ChoiceOption<CompatLaunchStrategy> _strategy;
    [ObservableProperty] private ChoiceOption<string?> _runner;
    [ObservableProperty] private string _extraEnvironment;
    [ObservableProperty] private string _extraDllOverrides;
    [ObservableProperty] private string _message = "";

    public SettingsViewModel(AppSettingsService settings)
    {
        _settings = settings;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Strategies =
        [
            new(L.StrategyUmu, CompatLaunchStrategy.Umu),
            new(L.StrategyProton, CompatLaunchStrategy.ProtonDirect),
            new(L.StrategySteam, CompatLaunchStrategy.ThroughSteam),
        ];
        Runners =
        [
            new(L.UmuAuto, null),
            new(L.GeLatest, CompatSettings.GeProtonLatest),
            .. CompatRunnerLocator.FindProtons(home, ShortcutHelper.GetSteamPath()).Select(p => new ChoiceOption<string?>(p.Name, p.Directory)),
        ];

        var current = settings.Compat;
        _strategy = Strategies.First(s => s.Value == current.Strategy);
        _runner = Runners.FirstOrDefault(r => r.Value == current.Runner)
                  ?? (current.Runner == null ? Runners[0] : new ChoiceOption<string?>(current.Runner, current.Runner));
        if (!Runners.Contains(_runner)) Runners.Add(_runner);
        _extraEnvironment = string.Join('\n', current.ExtraEnvironment.Select(kv => $"{kv.Key}={kv.Value}"));
        _extraDllOverrides = current.ExtraDllOverrides ?? "";
    }

    public IReadOnlyList<ChoiceOption<CompatLaunchStrategy>> Strategies { get; }
    public List<ChoiceOption<string?>> Runners { get; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var env = CompatSettings.ParseEnvironment(ExtraEnvironment, out var bad);
        if (bad != null)
        {
            Message = $"✗ {bad}";
            return;
        }
        await _settings.SetCompatAsync(new CompatSettings
        {
            Strategy = Strategy.Value,
            Runner = Runner.Value,
            ExtraEnvironment = env,
            ExtraDllOverrides = string.IsNullOrWhiteSpace(ExtraDllOverrides) ? null : ExtraDllOverrides.Trim(),
        });
        Message = L.Saved;
    }
}
