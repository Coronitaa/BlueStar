using Avalonia;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure;
using BlueStar.Infrastructure.Launcher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BlueStar.Linux;

public static class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        Services = BuildServices(console: args.Contains(HeadlessLaunch.Switch) || args.Contains(SelfTest.Switch));
        try
        {
            var launchAt = Array.IndexOf(args, HeadlessLaunch.Switch);
            if (launchAt >= 0)
            {
                if (launchAt + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Usage: BlueStar --launch <instance id>");
                    return HeadlessLaunch.ExitBadId;
                }
                return HeadlessLaunch.RunAsync(
                    Services.GetRequiredService<IInstanceManager>(),
                    Services.GetRequiredService<IGameLauncher>(),
                    args[launchAt + 1],
                    Console.Out).GetAwaiter().GetResult();
            }

            var selfTestAt = Array.IndexOf(args, SelfTest.Switch);
            if (selfTestAt >= 0)
                SelfTest.Start(selfTestAt + 1 < args.Length ? args[selfTestAt + 1] : null);

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static IServiceProvider BuildServices(bool console)
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "logs");
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(logs, "bluestar-linux-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7);
        if (console)
            config = config.WriteTo.Console(Serilog.Events.LogEventLevel.Information);
        Log.Logger = config.CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddBlueStar();
        services.AddSingleton<ViewModels.LibraryViewModel>();
        services.AddSingleton<ViewModels.SettingsViewModel>();
        services.AddSingleton<ViewModels.RequirementsViewModel>();
        services.AddSingleton<ViewModels.MainViewModel>();
        return services.BuildServiceProvider();
    }
}
