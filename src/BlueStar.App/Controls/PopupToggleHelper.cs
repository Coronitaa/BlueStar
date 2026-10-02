using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BlueStar.App.Controls;

/// <summary>
/// Attached helper for <see cref="ToggleButton"/> paired with a <see cref="Popup"/> having StaysOpen="False".
/// Prevents the classic WPF quirk where clicking the ToggleButton while the popup is open causes
/// the popup's capture release to close it, and the subsequent button click to immediately reopen it.
/// </summary>
public static class PopupToggleHelper
{
    public static readonly DependencyProperty PopupProperty =
        DependencyProperty.RegisterAttached(
            "Popup",
            typeof(Popup),
            typeof(PopupToggleHelper),
            new PropertyMetadata(null, OnPopupChanged));

    public static Popup? GetPopup(DependencyObject obj) => (Popup?)obj.GetValue(PopupProperty);
    public static void SetPopup(DependencyObject obj, Popup? value) => obj.SetValue(PopupProperty, value);

    private static void OnPopupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton toggle) return;

        if (e.OldValue is Popup oldPopup)
        {
            oldPopup.Closed -= OnPopupClosed;
            toggle.PreviewMouseDown -= OnTogglePreviewMouseDown;
        }

        if (e.NewValue is Popup newPopup)
        {
            newPopup.Closed += OnPopupClosed;
            toggle.PreviewMouseDown += OnTogglePreviewMouseDown;
        }
    }

    private static DateTime _lastClosed = DateTime.MinValue;

    private static void OnPopupClosed(object? sender, EventArgs e)
    {
        _lastClosed = DateTime.UtcNow;
    }

    private static void OnTogglePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((DateTime.UtcNow - _lastClosed).TotalMilliseconds < 250)
        {
            // Popup was closed by this click. Prevent ToggleButton from re-opening it.
            e.Handled = true;
        }
    }
}
