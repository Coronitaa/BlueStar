using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlueStar.App.Controls;

/// <summary>
/// A lightweight ContentControl that smoothly animates view changes using a subtle
/// fade-in and slide-up transition without interrupting navigation or freezing the UI thread.
/// Inspired by Wpf.Ui and modern XAML transition patterns.
/// </summary>
[TemplatePart(Name = PartContentPresenter, Type = typeof(ContentPresenter))]
public class TransitioningContentControl : ContentControl
{
    public const string PartContentPresenter = "PART_ContentPresenter";

    private ContentPresenter? _contentPresenter;
    private TranslateTransform? _translateTransform;
    private int _transitionToken;

    static TransitioningContentControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TransitioningContentControl),
            new FrameworkPropertyMetadata(typeof(TransitioningContentControl)));
    }

    public TransitioningContentControl()
    {
        IsTabStop = false;
        Focusable = false;
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty TransitionDurationProperty =
        DependencyProperty.Register(
            nameof(TransitionDuration),
            typeof(TimeSpan),
            typeof(TransitioningContentControl),
            new PropertyMetadata(TimeSpan.FromMilliseconds(200)));

    public TimeSpan TransitionDuration
    {
        get => (TimeSpan)GetValue(TransitionDurationProperty);
        set => SetValue(TransitionDurationProperty, value);
    }

    public static readonly DependencyProperty TransitionOffsetProperty =
        DependencyProperty.Register(
            nameof(TransitionOffset),
            typeof(double),
            typeof(TransitioningContentControl),
            new PropertyMetadata(8.0));

    public double TransitionOffset
    {
        get => (double)GetValue(TransitionOffsetProperty);
        set => SetValue(TransitionOffsetProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _contentPresenter = GetTemplateChild(PartContentPresenter) as ContentPresenter;
        if (_contentPresenter != null)
        {
            _translateTransform = new TranslateTransform(0, 0);
            _contentPresenter.RenderTransform = _translateTransform;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_contentPresenter != null)
        {
            _contentPresenter.Opacity = 1.0;
            if (_translateTransform != null)
            {
                _translateTransform.Y = 0.0;
            }
        }
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (_contentPresenter == null || newContent == null)
        {
            return;
        }

        // If not loaded yet, snap directly to avoid invisible initial render
        if (!IsLoaded)
        {
            _contentPresenter.BeginAnimation(OpacityProperty, null);
            _contentPresenter.Opacity = 1.0;
            if (_translateTransform != null)
            {
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _translateTransform.Y = 0.0;
            }
            return;
        }

        // Stop any running animations and set initial state for new content
        _contentPresenter.BeginAnimation(OpacityProperty, null);
        _contentPresenter.Opacity = 0.0;

        if (_translateTransform != null)
        {
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _translateTransform.Y = TransitionOffset;
        }

        int currentToken = ++_transitionToken;

        // Animate on next render frame after layout pass has completed
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            if (currentToken != _transitionToken || _contentPresenter == null)
            {
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var opacityAnim = new DoubleAnimation(0.0, 1.0, TransitionDuration)
            {
                EasingFunction = ease
            };

            var translateAnim = new DoubleAnimation(TransitionOffset, 0.0, TransitionDuration)
            {
                EasingFunction = ease
            };

            opacityAnim.Completed += (s, e) =>
            {
                if (currentToken == _transitionToken && _contentPresenter != null)
                {
                    _contentPresenter.BeginAnimation(OpacityProperty, null);
                    _contentPresenter.Opacity = 1.0;
                    if (_translateTransform != null)
                    {
                        _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
                        _translateTransform.Y = 0.0;
                    }
                }
            };

            _contentPresenter.BeginAnimation(OpacityProperty, opacityAnim);

            if (_translateTransform != null)
            {
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, translateAnim);
            }
        });
    }
}
