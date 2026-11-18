using Avalonia.Controls;
using Avalonia.Threading;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Platform;
using BlueStar.Linux.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BlueStar.Linux;

/// <summary>
/// <c>BlueStar --selftest [game.exe]</c>, for CI under xvfb. Imports a game folder, launches it through the compat
/// runtime when an exe is given (the exe must write <c>bluestar-selftest.ok</c> next to itself), opens the real
/// window, checks the library shows the game, prints PASS/FAIL lines and exits 0 only when every check passes.
/// Run it with a throwaway HOME: it writes BlueStar's normal config there.
/// </summary>
public static class SelfTest
{
    public const string Switch = "--selftest";
    private static readonly List<string> Failures = [];
    private static bool _active;
    private static string? _gameName;

    public static void Start(string? exe)
    {
        _active = true;
        foreach (var r in LinuxRequirements.Check())
            Console.WriteLine($"[req] {(r.Ok ? "ok  " : "MISS")} {r.Name}: {r.Detail}");

        try { PrepareAsync(exe).GetAwaiter().GetResult(); }
        catch (Exception ex) { Report("prepare", false, ex.ToString()); }
    }

    private static async Task PrepareAsync(string? exe)
    {
        var folder = exe != null ? Path.GetDirectoryName(Path.GetFullPath(exe))! : Directory.CreateTempSubdirectory("bs-selftest-").FullName;
        if (exe == null)
            File.WriteAllText(Path.Combine(folder, "Game.exe"), "MZ");

        var import = Program.Services.GetRequiredService<GameImportService>();
        var instance = await import.ImportFolderAsync(folder, "BlueStar SelfTest");
        _gameName = instance.Name;
        Report("import folder", instance.ExecutablePath != null, instance.ExecutablePath ?? "no exe found");

        var again = await Record(() => import.ImportFolderAsync(folder));
        Report("duplicate folder rejected", again is InvalidOperationException, again?.Message ?? "accepted twice");

        if (exe == null) return;

        var marker = Path.Combine(folder, "bluestar-selftest.ok");
        File.Delete(marker);
        var launcher = Program.Services.GetRequiredService<IGameLauncher>();
        var exited = new TaskCompletionSource();
        launcher.RunningStateChanged += (_, e) => { if (e.InstanceId == instance.Id && !e.IsRunning) exited.TrySetResult(); };
        var result = await launcher.LaunchAsync(instance, line => Console.WriteLine($"[launch] {line}"));
        Report("launch", result.Success, result.Message);
        if (result.Success && launcher.IsRunning(instance.Id))
            await exited.Task.WaitAsync(TimeSpan.FromMinutes(15));
        Report("game ran under Proton", File.Exists(marker), marker);
    }

    public static async Task OnWindowReadyAsync(Window window, MainViewModel vm)
    {
        if (!_active) return;
        Report("window opened", window.IsVisible, window.Title ?? "");
        var game = vm.Library.Games.FirstOrDefault(g => g.Name == _gameName);
        Report("library shows game", game != null, $"{vm.Library.Games.Count} games");
        Report("requirements listed", vm.Requirements.Rows.Count > 0, $"{vm.Requirements.Rows.Count} rows");
        Report("settings loaded", vm.Settings.Runners.Count >= 2, $"{vm.Settings.Runners.Count} runners");
        if (game != null)
        {
            await vm.Library.RemoveCommand.ExecuteAsync(game);
            var exe = game.Instance.ExecutablePath;
            Report("remove keeps game files", !vm.Library.Games.Contains(game) && File.Exists(exe), exe ?? "no exe");
        }
        Console.WriteLine(Failures.Count == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL: {string.Join(", ", Failures)}");
        Dispatcher.UIThread.Post(() => Environment.Exit(Failures.Count == 0 ? 0 : 1));
    }

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static void Report(string check, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {check}: {detail}");
        if (!ok) Failures.Add(check);
    }
}
