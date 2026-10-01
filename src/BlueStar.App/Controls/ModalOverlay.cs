using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlueStar.App.Controls;

/// <summary>
/// A reusable modal overlay control that manages the visual lifecycle (fade-in/fade-out,
/// scale entrance/exit, hit-testing, backdrop clicking, and Escape-key closing) without
/// abrupt visibility cutoffs.
/// </summary>
[TemplatePart(Name = PartRoot, Type = typeof(Grid))]
[TemplatePart(Name = PartBackdrop, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartContentHolder, Type = typeof(FrameworkElement))]
public class ModalOverlay : ContentControl
{
    public const string PartRoot = "PART_Root";
    public const string PartBackdrop = "PART_Backdrop";
    public const string PartContentHolder = "PART_ContentHolder";

    private static readonly List<ModalOverlay> ActiveModals = new();

    private FrameworkElement? _backdrop;
    private FrameworkElement? _contentHolder;
    private ScaleTransform? _scaleTransform;
    private Window? _parentWindow;
    private int _animationToken;

    static ModalOverlay()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ModalOverlay),
            new FrameworkPropertyMetadata(typeof(ModalOverlay)));
    }

    public ModalOverlay()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    #region Dependency Properties

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen),
            typeof(bool),
            typeof(ModalOverlay),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly DependencyProperty BackdropBrushProperty =
        DependencyProperty.Register(
            nameof(BackdropBrush),
            typeof(Brush),
            typeof(ModalOverlay),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00))));

    public Brush BackdropBrush
    {
        get => (Brush)GetValue(BackdropBrushProperty);
        set => SetValue(BackdropBrushProperty, value);
    }

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(
            nameof(CloseCommand),
            typeof(ICommand),
            typeof(ModalOverlay),
            new PropertyMetadata(null));

    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public static readonly DependencyProperty CloseCommandParameterProperty =
        DependencyProperty.Register(
            nameof(CloseCommandParameter),
            typeof(object),
            typeof(ModalOverlay),
            new PropertyMetadata(null));

    public object? CloseCommandParameter
    {
        get => GetValue(CloseCommandParameterProperty);
        set => SetValue(CloseCommandParameterProperty, value);
    }

    public static readonly DependencyProperty CloseOnBackdropClickProperty =
        DependencyProperty.Register(
            nameof(CloseOnBackdropClick),
            typeof(bool),
            typeof(ModalOverlay),
            new PropertyMetadata(false));

    public bool CloseOnBackdropClick
    {
        get => (bool)GetValue(CloseOnBackdropClickProperty);
        set => SetValue(CloseOnBackdropClickProperty, value);
    }

    public static readonly DependencyProperty CloseOnEscapeProperty =
        DependencyProperty.Register(
            nameof(CloseOnEscape),
            typeof(bool),
            typeof(ModalOverlay),
            new PropertyMetadata(true));

    public bool CloseOnEscape
    {
        get => (bool)GetValue(CloseOnEscapeProperty);
        set => SetValue(CloseOnEscapeProperty, value);
    }

    #endregion

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_backdrop != null)
        {
            _backdrop.MouseLeftButtonDown -= OnBackdropMouseLeftButtonDown;
        }

        _backdrop = GetTemplateChild(PartBackdrop) as FrameworkElement;
        _contentHolder = GetTemplateChild(PartContentHolder) as FrameworkElement;

        if (_backdrop != null)
        {
            _backdrop.MouseLeftButtonDown += OnBackdropMouseLeftButtonDown;
        }

        if (_contentHolder != null)
        {
            if (_contentHolder.RenderTransform is ScaleTransform st)
            {
                _scaleTransform = st;
            }
            else
            {
                _scaleTransform = new ScaleTransform(1.0, 1.0);
                _contentHolder.RenderTransform = _scaleTransform;
                _contentHolder.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }

        // Apply initial visual state without transition
        ApplyInitialState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _parentWindow = Window.GetWindow(this);
        if (_parentWindow != null)
        {
            _parentWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _parentWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        if (IsOpen && !ActiveModals.Contains(this))
        {
            ActiveModals.Add(this);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_parentWindow != null)
        {
            _parentWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _parentWindow = null;
        }

        ActiveModals.Remove(this);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModalOverlay overlay)
        {
            bool isOpen = (bool)e.NewValue;
            if (isOpen)
            {
                if (!ActiveModals.Contains(overlay))
                {
                    ActiveModals.Add(overlay);
                }
            }
            else
            {
                ActiveModals.Remove(overlay);
            }

            overlay.UpdateVisualState(useTransitions: overlay.IsLoaded);
        }
    }

    private void ApplyInitialState()
    {
        if (IsOpen)
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            if (_backdrop != null) _backdrop.Opacity = 1.0;
            if (_contentHolder != null) _contentHolder.Opacity = 1.0;
            if (_scaleTransform != null)
            {
                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
            }
        }
        else
        {
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            if (_backdrop != null) _backdrop.Opacity = 0.0;
            if (_contentHolder != null) _contentHolder.Opacity = 0.0;
            if (_scaleTransform != null)
            {
                _scaleTransform.ScaleX = 0.96;
                _scaleTransform.ScaleY = 0.96;
            }
        }
    }

    private void UpdateVisualState(bool useTransitions)
    {
        if (!IsLoaded || _backdrop == null || _contentHolder == null)
        {
            ApplyInitialState();
            return;
        }

        _animationToken++;
        int currentToken = _animationToken;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (IsOpen)
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;

            var duration = TimeSpan.FromMilliseconds(220); // DurationNormal

            var backdropAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };
            var contentOpacityAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };
            var scaleXAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };
            var scaleYAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };

            _backdrop.BeginAnimation(OpacityProperty, backdropAnim);
            _contentHolder.BeginAnimation(OpacityProperty, contentOpacityAnim);

            if (_scaleTransform != null)
            {
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            }

            _contentHolder.Focus();
        }
        else
        {
            IsHitTestVisible = false;

            if (!useTransitions)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            var duration = TimeSpan.FromMilliseconds(150); // DurationFast

            var backdropAnim = new DoubleAnimation(0.0, duration) { EasingFunction = easeOut };
            var contentOpacityAnim = new DoubleAnimation(0.0, duration) { EasingFunction = easeOut };
            var scaleXAnim = new DoubleAnimation(0.96, duration) { EasingFunction = easeOut };
            var scaleYAnim = new DoubleAnimation(0.96, duration) { EasingFunction = easeOut };

            contentOpacityAnim.Completed += (s, e) =>
            {
                if (currentToken == _animationToken && !IsOpen)
                {
                    Visibility = Visibility.Collapsed;
                }
            };

            _backdrop.BeginAnimation(OpacityProperty, backdropAnim);
            _contentHolder.BeginAnimation(OpacityProperty, contentOpacityAnim);

            if (_scaleTransform != null)
            {
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            }
        }
    }

    private void OnBackdropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CloseOnBackdropClick && IsOpen)
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen && CloseOnEscape)
        {
            // Only topmost active modal handles Escape
            if (ActiveModals.Count > 0 && ActiveModals[^1] == this)
            {
                RequestClose();
                e.Handled = true;
            }
        }
    }

    public void RequestClose()
    {
        if (CloseCommand != null && CloseCommand.CanExecute(CloseCommandParameter))
        {
            CloseCommand.Execute(CloseCommandParameter);
        }
        else
        {
            SetCurrentValue(IsOpenProperty, false);
        }
    }
}
