using System.IO;
using System.IO.Pipes;
using System.Reflection;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure;
using BlueStar.Infrastructure.Launcher;
using BlueStar.Infrastructure.Workshop;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BlueStar.App;

/// <summary>
/// Entry point. Besides starting the WPF app, BlueStar.exe hosts the DepotDownloader command line used for Workshop
/// downloads, so it runs on BlueStar's own self-contained .NET runtime and needs no system-wide .NET install
/// (a fresh Windows and the Microsoft Store package have none).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == DepotDownloaderHost.HostSwitch)
        {
            return RunDepotDownloader(args[1..]);
        }

        if (args.Length > 1 && args[0] == HeadlessLaunch.Switch)
        {
            return RunHeadlessLaunch(args[1]);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>No UI and no single-instance mutex: a running BlueStar window must not block a Steam shortcut.</summary>
    private static int RunHeadlessLaunch(string instanceId)
    {
        DepotDownloaderHost.HostExecutablePath = Environment.ProcessPath;
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "logs", "bluestar-launch-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(logger, dispose: true));
        services.AddSingleton<IDebugLogService>(new BlueStar.Infrastructure.Services.DebugLogService());
        services.AddBlueStar();
        using var provider = services.BuildServiceProvider();

        return HeadlessLaunch.RunAsync(
                provider.GetRequiredService<IInstanceManager>(),
                provider.GetRequiredService<IGameLauncher>(),
                instanceId,
                Console.Out,
                NotifyRunningUi)
            .GetAwaiter().GetResult();
    }

    private static void NotifyRunningUi(Guid instanceId)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", App.IpcPipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(HeadlessLaunch.InstanceChangedMessage + instanceId);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            // No BlueStar window open: nothing to refresh.
        }
    }

    private static int RunDepotDownloader(string[] args)
    {
        try
        {
            return DepotDownloaderHost.RunCommandLine(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }
}
