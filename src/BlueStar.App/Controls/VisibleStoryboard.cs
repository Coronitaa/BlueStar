using System.Windows;
using System.Windows.Media.Animation;

namespace BlueStar.App.Controls;

/// <summary>
/// Runs a looping storyboard (looked up by resource key) only while the element is visible.
/// </summary>
/// <remarks>
/// A <c>RepeatBehavior="Forever"</c> storyboard started on Loaded keeps its clock running after its overlay is
/// collapsed, and a running clock makes WPF render a frame 60 times a second for the rest of the session.
/// </remarks>
public static class VisibleStoryboard
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(VisibleStoryboard), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.IsVisibleChanged -= OnIsVisibleChanged;
        element.IsVisibleChanged += OnIsVisibleChanged;
        Update(element);
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        if (element.TryFindResource(GetKey(element) ?? string.Empty) is not Storyboard storyboard) return;

        if (element.IsVisible)
        {
            storyboard.Begin(element, isControllable: true);
        }
        else
        {
            storyboard.Stop(element);
        }
    }
}
