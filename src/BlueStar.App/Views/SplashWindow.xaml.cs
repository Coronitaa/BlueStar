using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BlueStar.App.Views;

/// <summary>
/// Startup splash. Shown before anything else is built so the app never sits as a blank taskbar
/// entry, and faded out once the main window has actually rendered.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3);
            if (!string.IsNullOrWhiteSpace(version)) VersionText.Text = version;
        }
        catch
        {
            // Version is decoration; never let it stop startup.
        }
    }

    /// <summary>
    /// Updates the status line and pumps a render pass, so the message is actually painted even
    /// though startup is running synchronously on this same dispatcher thread.
    /// </summary>
    public void Report(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Report(message));
            return;
        }

        StatusText.Text = message;
        PumpRender();
    }

    /// <summary>Forces a layout + render pass on the current dispatcher frame.</summary>
    public void PumpRender()
    {
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
        catch
        {
            // A failed pump only costs a frame.
        }
    }

    /// <summary>
    /// Fades the splash out and closes it. <paramref name="onFinished"/> runs after the window is
    /// gone, so the caller can hand ownership of the app lifetime to the main window.
    /// </summary>
    public void FadeOutAndClose(Action? onFinished = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => FadeOutAndClose(onFinished));
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { }
            onFinished?.Invoke();
        };

        BeginAnimation(OpacityProperty, fade);
    }
}
