using System.Reflection;
using BlueStar.Infrastructure.Workshop;

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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
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
