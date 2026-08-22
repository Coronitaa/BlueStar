using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueStar.App.Controls;

/// <summary>
/// A circular progress indicator that draws an arc proportional to the Percentage property.
/// </summary>
public partial class CircularProgressButton : UserControl
{
    public static readonly DependencyProperty PercentageProperty =
        DependencyProperty.Register(nameof(Percentage), typeof(double), typeof(CircularProgressButton),
            new PropertyMetadata(0.0, OnProgressChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(CircularProgressButton),
            new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(CircularProgressButton),
            new PropertyMetadata("Download", OnStateChanged));

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public CircularProgressButton()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisuals();
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularProgressButton)d).UpdateArc();

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularProgressButton)d).UpdateVisuals();

    private void UpdateVisuals()
    {
        var active = IsActive;
        GlowRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        IdleIcon.Visibility  = active ? Visibility.Collapsed : Visibility.Visible;
        PercentLabel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // Parse out a number from StatusText to show in the percentage label
        var pct = Percentage;
        PercentLabel.Text = $"{pct:F0}%";
        StatusLabel.Text  = active ? "Downloading" : StatusText;

        UpdateArc();
    }

    private void UpdateArc()
    {
        double pct = Math.Clamp(Percentage, 0, 100);
        if (pct < 0.5)
        {
            ArcPath.Data = null;
            return;
        }

        const double cx = 48, cy = 48, r = 42;
        double angle = pct / 100.0 * 360.0 - 0.01; // avoid full-circle degenerate case
        bool largeArc = angle > 180;

        double rad = (angle - 90) * Math.PI / 180.0;
        double x = cx + r * Math.Cos(-Math.PI / 2);      // start = top
        double y = cy + r * Math.Sin(-Math.PI / 2);

        double ex = cx + r * Math.Cos(rad - Math.PI / 2 + Math.PI / 2);
        double ey = cy + r * Math.Sin(rad - Math.PI / 2 + Math.PI / 2);

        // Recalculate correctly
        double startRad = -Math.PI / 2; // 12 o'clock
        double endRad   = startRad + (angle * Math.PI / 180.0);

        double sx = cx + r * Math.Cos(startRad);
        double sy = cy + r * Math.Sin(startRad);
        double ex2 = cx + r * Math.Cos(endRad);
        double ey2 = cy + r * Math.Sin(endRad);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(sx, sy), false, false);
            ctx.ArcTo(new Point(ex2, ey2), new Size(r, r), 0, largeArc, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        ArcPath.Data = geo;

        // Update percent label
        PercentLabel.Text = $"{pct:F0}%";
    }
}
