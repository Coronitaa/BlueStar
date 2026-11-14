using System.Diagnostics;
using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Compat;

/// <summary>
/// What to start for a Windows game on Linux. <see cref="SteamUrl"/> set means "hand it to Steam" (ThroughSteam):
/// BlueStar then cannot track the process.
/// </summary>
public sealed record CompatCommand(
    CompatLaunchStrategy Strategy,
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    CompatPrefix? Prefix,
    string? WineServer,
    string? Notice = null,
    string? SteamUrl = null);

/// <summary>Turns an instance into a umu / Proton / Steam launch, falling back to ProtonDirect when umu cannot run.</summary>
public sealed class CompatLauncher
{
    private readonly AppSettingsService _settings;
    private readonly ILogger<CompatLauncher> _logger;
    private readonly string _home;

    public CompatLauncher(AppSettingsService settings, ILogger<CompatLauncher> logger)
    {
        _settings = settings;
        _logger = logger;
        _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public CompatCommand Plan(GameInstance instance, string exePath)
    {
        var settings = _settings.Compat.WithOverride(instance.CompatOverride);
        var steamRoot = ShortcutHelper.GetSteamPath();
        var path = Environment.GetEnvironmentVariable("PATH");
        var umu = CompatRunnerLocator.FindUmuRun(_home, path);
        var health = CompatHealth.Check(umu, path, CompatHealth.ReadProcFile);
        var protons = CompatRunnerLocator.FindProtons(_home, steamRoot);
        return Plan(settings, instance, exePath, CompatPrefix.For(instance, _home, steamRoot), steamRoot ?? Path.Combine(_home, ".steam", "steam"), umu, health, protons);
    }

    /// <summary>Decision logic without touching the machine, for tests.</summary>
    public static CompatCommand Plan(
        CompatSettings settings,
        GameInstance instance,
        string exePath,
        CompatPrefix prefix,
        string steamRoot,
        string? umuRun,
        CompatHealth health,
        IReadOnlyList<ProtonInstall> protons)
    {
        if (settings.Strategy == CompatLaunchStrategy.ThroughSteam)
            return ThroughSteam(instance, exePath);

        var exeDir = Path.GetDirectoryName(exePath)!;
        var overrides = CompatEnvironmentBuilder.DllOverrides(CompatEnvironmentBuilder.DetectDllOverrides(exeDir), settings.ExtraDllOverrides);
        var chosenProton = ResolveProton(settings.Runner, protons);
        var args = new List<string> { exePath };
        args.AddRange(SplitArguments(instance.LaunchArguments));

        if (settings.Strategy == CompatLaunchStrategy.Umu && health.CanUseUmu)
        {
            var env = CompatEnvironmentBuilder.ForUmu(settings, instance, prefix, overrides);
            return new CompatCommand(CompatLaunchStrategy.Umu, umuRun!, args, env, prefix, chosenProton?.WineServer);
        }

        var notice = settings.Strategy == CompatLaunchStrategy.Umu
            ? $"Started with Proton directly because {health.FallbackReason}."
            : null;
        if (chosenProton == null)
            throw new InvalidOperationException("No Proton found. Install Proton from Steam (Library > Tools) or GE-Proton into compatibilitytools.d.");

        var protonEnv = CompatEnvironmentBuilder.ForProton(settings, instance, prefix, steamRoot, overrides);
        return new CompatCommand(
            CompatLaunchStrategy.ProtonDirect,
            chosenProton.Script,
            ["waitforexitandrun", .. args],
            protonEnv,
            prefix,
            chosenProton.WineServer,
            notice);
    }

    /// <summary>A Proton folder the user picked, else the newest Proton found. umu keywords pick the newest too.</summary>
    private static ProtonInstall? ResolveProton(string? runner, IReadOnlyList<ProtonInstall> protons)
    {
        if (!string.IsNullOrWhiteSpace(runner) && File.Exists(Path.Combine(runner, "proton")))
            return new ProtonInstall(Path.GetFileName(runner.TrimEnd('/')), runner);
        if (runner == CompatSettings.GeProtonLatest)
            return protons.FirstOrDefault(p => p.Name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase)) ?? protons.FirstOrDefault();
        return protons.FirstOrDefault();
    }

    /// <summary>
    /// Steam-owned games start by AppId. Anything else must already be a Steam shortcut ("Add to Steam") with the same
    /// exe and name: Steam derives the shortcut id from them, as <see cref="ShortcutHelper"/> does when writing it.
    /// </summary>
    private static CompatCommand ThroughSteam(GameInstance instance, string exePath)
    {
        ulong gameId = instance.Origin == InstanceOrigin.Steam && instance.AppId > 0
            ? instance.AppId
            : ((ulong)(ShortcutHelper.ComputeCrc32(exePath + instance.Name) | 0x80000000) << 32) | 0x02000000;
        var url = $"steam://rungameid/{gameId}";
        return new CompatCommand(CompatLaunchStrategy.ThroughSteam, "xdg-open", [url], new Dictionary<string, string>(), null, null, SteamUrl: url);
    }

    /// <summary>Splits launch options like a shell would for simple cases: spaces, with "double quoted" groups.</summary>
    public static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public ProcessStartInfo ToStartInfo(CompatCommand command, string workingDirectory)
    {
        command.Prefix?.EnsureCreated();
        var psi = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in command.Environment)
            psi.Environment[key] = value;

        if (command.Notice != null)
            _logger.LogWarning("{Notice}", command.Notice);
        _logger.LogInformation("Compat launch via {Strategy}: {File} {Args} (WINEPREFIX={Prefix})",
            command.Strategy, command.FileName, string.Join(' ', command.Arguments), command.Prefix?.WinePrefix);
        return psi;
    }

    /// <summary>
    /// Stops everything running in the game's prefix (launchers, crash handlers, the game) with <c>wineserver -k</c>,
    /// using the runner's own wineserver so the protocol versions match.
    /// </summary>
    public async Task StopAsync(CompatCommand command)
    {
        if (command.Prefix == null)
            return;

        var psi = new ProcessStartInfo(command.WineServer ?? "wineserver") { UseShellExecute = false };
        psi.ArgumentList.Add("-k");
        psi.Environment["WINEPREFIX"] = command.Prefix.WinePrefix;
        try
        {
            using var process = Process.Start(psi);
            if (process != null)
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            _logger.LogWarning(ex, "wineserver -k failed for prefix {Prefix}", command.Prefix.WinePrefix);
        }
    }
}
