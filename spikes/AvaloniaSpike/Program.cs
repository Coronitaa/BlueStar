using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AvaloniaSpike;

/// <summary>
/// Phase-0 spike. Proves what BlueStar needs from a WebView on Linux: cookie inject + read (Steam web session),
/// navigation cancel (Nexus / store link interception) and script exec. <c>--selftest</c> runs the checks,
/// prints PASS/FAIL lines and exits 0 only when all pass, so CI can run it under xvfb.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SpikeApp.SelfTest = args.Contains("--selftest");
        return AppBuilder.Configure<SpikeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class SpikeApp : Application
{
    public static bool SelfTest { get; set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SpikeWindow(desktop);
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class SpikeWindow : Window
{
    private const string Page = """
        <html><head><title>bluestar-spike</title></head>
        <body><a id="out" href="https://blocked.invalid/">blocked link</a></body></html>
        """;

    private static readonly Uri BaseUri = new("https://example.com/");

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly NativeWebView _web = new();
    private readonly TextBlock _log = new() { Margin = new Thickness(8) };
    private readonly List<string> _failures = [];
    private bool _blockedSeen;
    private bool _ran;

    public SpikeWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        Title = "BlueStar Avalonia spike";
        Width = 900;
        Height = 600;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(_log);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;

        _web.NavigationStarted += (_, e) =>
        {
            if (e.Request?.Host == "blocked.invalid")
            {
                _blockedSeen = true;
                e.Cancel = true;
            }
        };
        _web.NavigationCompleted += async (_, _) => await RunChecksOnceAsync();
        Opened += (_, _) =>
        {
            Report("adapter", true, DescribeAdapter());
            _web.NavigateToString(Page, BaseUri);
            DispatcherTimer.RunOnce(() => Finish("timeout"), TimeSpan.FromSeconds(45));
        };
    }

    private string DescribeAdapter()
    {
        var info = _web.AdapterInfo;
        return info == null ? "no adapter yet" : $"{info.Type} {info.Engine} {info.Version}";
    }

    private async Task RunChecksOnceAsync()
    {
        if (_ran) return;
        _ran = true;

        try
        {
            var title = await _web.InvokeScript("document.title");
            Report("script", title?.Contains("bluestar-spike") == true, $"document.title = {title}");

            var cookies = _web.TryGetCookieManager();
            if (cookies == null)
            {
                Report("cookies", false, "no cookie manager on this adapter");
            }
            else
            {
                cookies.AddOrUpdateCookie(new Cookie("steamLoginSecure", "spike-value", "/", "example.com"));
                var all = await cookies.GetCookiesAsync();
                Report("cookies", all.Any(c => c.Name == "steamLoginSecure" && c.Value == "spike-value"), $"{all.Count} cookie(s) read back");
            }

            await _web.InvokeScript("document.getElementById('out').click()");
            await Task.Delay(1500);
            var stillHere = await _web.InvokeScript("location.hostname");
            Report("nav-cancel", _blockedSeen && stillHere?.Contains("blocked") != true, $"blocked seen={_blockedSeen}, host={stillHere}");
        }
        catch (Exception ex)
        {
            Report("exception", false, ex.Message);
        }

        Finish(null);
    }

    private void Report(string check, bool ok, string detail)
    {
        if (!ok) _failures.Add(check);
        var line = $"{(ok ? "PASS" : "FAIL")} {check}: {detail}";
        Console.WriteLine(line);
        _log.Text += line + Environment.NewLine;
    }

    private void Finish(string? reason)
    {
        if (reason != null && !_ran) Report(reason, false, "checks never ran (no NavigationCompleted)");
        if (!SpikeApp.SelfTest) return;
        Console.WriteLine(_failures.Count == 0 ? "SPIKE OK" : $"SPIKE FAILED: {string.Join(", ", _failures)}");
        _desktop.Shutdown(_failures.Count == 0 ? 0 : 1);
    }
}
