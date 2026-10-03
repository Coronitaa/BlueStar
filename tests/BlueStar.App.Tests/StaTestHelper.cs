using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace BlueStar.App.Tests;

public static class StaTestHelper
{
    private static readonly Dispatcher _dispatcher;

    static StaTestHelper()
    {
        var readyEvent = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.ResourceAssembly == null)
                {
                    Application.ResourceAssembly = typeof(BlueStar.App.App).Assembly;
                }

                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var dicts = new[]
                {
                    "pack://application:,,,/BlueStar;component/Themes/Colors.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Icons.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Typography.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Motion.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Controls.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/DarkTheme.xaml",
                    "pack://application:,,,/BlueStar;component/Themes/Strings.en.xaml"
                };

                foreach (var uri in dicts)
                {
                    try
                    {
                        var rd = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
                        app.Resources.MergedDictionaries.Add(rd);
                    }
                    catch { }
                }

                // Fallbacks for any missing tokens in test runner
                app.Resources["RadiusSm"] ??= new CornerRadius(4);
                app.Resources["RadiusMd"] ??= new CornerRadius(8);
                app.Resources["RadiusLg"] ??= new CornerRadius(12);
                app.Resources["RadiusXl"] ??= new CornerRadius(16);
                app.Resources["RadiusFull"] ??= new CornerRadius(9999);

                app.Resources["BaseBackgroundBrush"] ??= new SolidColorBrush(Color.FromRgb(10, 10, 14));
                app.Resources["PrimaryTextBrush"] ??= new SolidColorBrush(Color.FromRgb(255, 255, 255));
                app.Resources["SecondaryTextBrush"] ??= new SolidColorBrush(Color.FromRgb(160, 160, 170));
                app.Resources["MutedTextBrush"] ??= new SolidColorBrush(Color.FromRgb(100, 100, 110));
                app.Resources["SubtleBorderBrush"] ??= new SolidColorBrush(Color.FromRgb(30, 30, 40));
                app.Resources["SurfaceCardBrush"] ??= new SolidColorBrush(Color.FromRgb(20, 20, 28));
                app.Resources["SurfaceCardHoverBrush"] ??= new SolidColorBrush(Color.FromRgb(30, 30, 42));
                app.Resources["SurfaceCardElevatedBrush"] ??= new SolidColorBrush(Color.FromRgb(26, 26, 36));
                app.Resources["AccentBrush"] ??= new SolidColorBrush(Color.FromRgb(59, 130, 246));
                app.Resources["MainFontFamily"] ??= new FontFamily("Segoe UI");
            }
            catch { }

            dispatcher = Dispatcher.CurrentDispatcher;
            readyEvent.Set();

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        readyEvent.Wait();
        _dispatcher = dispatcher!;
    }

    public static void Run(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }
}
