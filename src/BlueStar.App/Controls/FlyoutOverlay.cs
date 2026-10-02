using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlueStar.App.Controls;

/// <summary>
/// A reusable flyout/dropdown container control that manages animated open/close lifecycle
/// (fade-in, slide-up/down, backdrop click-to-close, and clean collapse after exit animation).
/// Prevents premature clipping or stuck states during rapid interruptions.
/// </summary>
[TemplatePart(Name = PartRoot, Type = typeof(Grid))]
[TemplatePart(Name = PartBackdrop, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartContentHolder, Type = typeof(FrameworkElement))]
public class FlyoutOverlay : ContentControl
{
    public const string PartRoot = "PART_Root";
    public const string PartBackdrop = "PART_Backdrop";
    public const string PartContentHolder = "PART_ContentHolder";

    private FrameworkElement? _backdrop;
    private FrameworkElement? _contentHolder;
    private TranslateTransform? _translateTransform;
    private int _animationToken;
    private bool _pendingOpenTransition;

    static FlyoutOverlay()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FlyoutOverlay),
            new FrameworkPropertyMetadata(typeof(FlyoutOverlay)));
    }

    public FlyoutOverlay()
    {
        Loaded += OnLoaded;
    }

    #region Dependency Properties

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen),
            typeof(bool),
            typeof(FlyoutOverlay),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(
            nameof(CloseCommand),
            typeof(ICommand),
            typeof(FlyoutOverlay),
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
            typeof(FlyoutOverlay),
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
            typeof(FlyoutOverlay),
            new PropertyMetadata(true));

    public bool CloseOnBackdropClick
    {
        get => (bool)GetValue(CloseOnBackdropClickProperty);
        set => SetValue(CloseOnBackdropClickProperty, value);
    }

    public static readonly DependencyProperty SlideOffsetProperty =
        DependencyProperty.Register(
            nameof(SlideOffset),
            typeof(double),
            typeof(FlyoutOverlay),
            new PropertyMetadata(8.0));

    public double SlideOffset
    {
        get => (double)GetValue(SlideOffsetProperty);
        set => SetValue(SlideOffsetProperty, value);
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
            _backdrop.Opacity = 0.0;
        }

        if (_contentHolder != null)
        {
            _translateTransform = new TranslateTransform(0, SlideOffset);
            _contentHolder.RenderTransform = _translateTransform;
            _contentHolder.Opacity = 0.0;
        }

        if (!IsOpen)
        {
            ApplyInitialClosedState();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsOpen && (_pendingOpenTransition || _contentHolder?.Opacity == 0.0))
        {
            _pendingOpenTransition = false;
            UpdateVisualState(useTransitions: true);
        }
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlyoutOverlay overlay)
        {
            bool isOpen = (bool)e.NewValue;
            if (isOpen)
            {
                overlay.Visibility = Visibility.Visible;
                overlay.IsHitTestVisible = true;

                if (overlay._backdrop == null || overlay._contentHolder == null)
                {
                    overlay.ApplyTemplate();
                }

                if (overlay.IsLoaded)
                {
                    overlay.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                    {
                        if (overlay.IsOpen)
                        {
                            overlay.UpdateVisualState(useTransitions: true);
                        }
                    });
                }
                else
                {
                    overlay._pendingOpenTransition = true;
                    overlay.UpdateVisualState(useTransitions: false);
                }
            }
            else
            {
                overlay._pendingOpenTransition = false;
                overlay.IsHitTestVisible = false;
                overlay.UpdateVisualState(useTransitions: overlay.IsLoaded);
            }
        }
    }

    private void ApplyInitialClosedState()
    {
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;

        if (_backdrop != null)
        {
            _backdrop.BeginAnimation(OpacityProperty, null);
            _backdrop.Opacity = 0.0;
        }

        if (_contentHolder != null)
        {
            _contentHolder.BeginAnimation(OpacityProperty, null);
            _contentHolder.Opacity = 0.0;
        }

        if (_translateTransform != null)
        {
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _translateTransform.Y = SlideOffset;
        }
    }

    private void UpdateVisualState(bool useTransitions)
    {
        if (_backdrop == null || _contentHolder == null)
        {
            if (!IsOpen) ApplyInitialClosedState();
            return;
        }

        _animationToken++;
        int currentToken = _animationToken;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (IsOpen)
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;

            if (!useTransitions)
            {
                _backdrop.BeginAnimation(OpacityProperty, null);
                _backdrop.Opacity = 1.0;
                _contentHolder.BeginAnimation(OpacityProperty, null);
                _contentHolder.Opacity = 1.0;
                if (_translateTransform != null)
                {
                    _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
                    _translateTransform.Y = 0.0;
                }
                return;
            }

            var duration = TimeSpan.FromMilliseconds(200);

            var backdropAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };
            var contentOpacityAnim = new DoubleAnimation(1.0, duration) { EasingFunction = easeOut };

            _backdrop.BeginAnimation(OpacityProperty, backdropAnim);
            _contentHolder.BeginAnimation(OpacityProperty, contentOpacityAnim);

            if (_translateTransform != null)
            {
                var translateYAnim = new DoubleAnimation(0.0, duration) { EasingFunction = easeOut };
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, translateYAnim);
            }
        }
        else
        {
            IsHitTestVisible = false;

            if (!useTransitions)
            {
                ApplyInitialClosedState();
                return;
            }

            var duration = TimeSpan.FromMilliseconds(150);

            var backdropAnim = new DoubleAnimation(0.0, duration) { EasingFunction = easeOut };
            var contentOpacityAnim = new DoubleAnimation(0.0, duration) { EasingFunction = easeOut };

            contentOpacityAnim.Completed += (s, e) =>
            {
                if (currentToken == _animationToken && !IsOpen)
                {
                    Visibility = Visibility.Collapsed;
                }
            };

            _backdrop.BeginAnimation(OpacityProperty, backdropAnim);
            _contentHolder.BeginAnimation(OpacityProperty, contentOpacityAnim);

            if (_translateTransform != null)
            {
                var translateYAnim = new DoubleAnimation(SlideOffset, duration) { EasingFunction = easeOut };
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, translateYAnim);
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
