using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlueStar.App.Controls;

/// <summary>
/// Lets a <see cref="RadioButton"/> be switched back off by clicking the one that is already on.
/// </summary>
/// <remarks>
/// A radio group normally has no way back to "nothing selected": once an option is picked the
/// person can only move the dot, never clear it. The install modal needs that empty state — no
/// DLC unlocker, no emulator, no pinned version mode — so clicking the selected card again
/// pushes <c>false</c> through the binding and the ViewModel clears its choice.
/// </remarks>
public static class ToggleableRadio
{
    public static readonly DependencyProperty AllowUncheckProperty =
        DependencyProperty.RegisterAttached(
            "AllowUncheck", typeof(bool), typeof(ToggleableRadio),
            new PropertyMetadata(false, OnAllowUncheckChanged));

    public static bool GetAllowUncheck(DependencyObject element) =>
        (bool)element.GetValue(AllowUncheckProperty);

    public static void SetAllowUncheck(DependencyObject element, bool value) =>
        element.SetValue(AllowUncheckProperty, value);

    private static void OnAllowUncheckChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadioButton radio) return;

        radio.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;

        if (e.NewValue is true)
        {
            radio.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RadioButton radio || radio.IsChecked != true) return;

        // WPF swallows a click on an already-checked radio, so the uncheck has to happen here,
        // before the control sees the event.
        radio.IsChecked = false;
        e.Handled = true;
    }
}
