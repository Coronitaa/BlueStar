using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BlueStar.App.ViewModels;
using BlueStar.Core.Helpers;

namespace BlueStar.App.Controls;

/// <summary>
/// Interactive viewport control for displaying and editing Steam grid artworks
/// supporting click-to-browse and drag-and-drop image replacements with dimension validation.
/// </summary>
public partial class ArtworkViewportControl : UserControl
{
    public static readonly DependencyProperty SlotProperty =
        DependencyProperty.Register(
            nameof(Slot),
            typeof(ArtworkSlotViewModel),
            typeof(ArtworkViewportControl),
            new PropertyMetadata(null, OnSlotChanged));

    public static readonly DependencyProperty StretchModeProperty =
        DependencyProperty.Register(
            nameof(StretchMode),
            typeof(Stretch),
            typeof(ArtworkViewportControl),
            new PropertyMetadata(Stretch.UniformToFill));

    public static readonly DependencyProperty ShowCheckerboardProperty =
        DependencyProperty.Register(
            nameof(ShowCheckerboard),
            typeof(bool),
            typeof(ArtworkViewportControl),
            new PropertyMetadata(false, OnShowCheckerboardChanged));

    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(ArtworkViewportControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public ArtworkSlotViewModel? Slot
    {
        get => (ArtworkSlotViewModel?)GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    public Stretch StretchMode
    {
        get => (Stretch)GetValue(StretchModeProperty);
        set => SetValue(StretchModeProperty, value);
    }

    public bool ShowCheckerboard
    {
        get => (bool)GetValue(ShowCheckerboardProperty);
        set => SetValue(ShowCheckerboardProperty, value);
    }

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public ArtworkViewportControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Slot != null)
        {
            Slot.PropertyChanged -= OnSlotPropertyChanged;
            Slot.PropertyChanged += OnSlotPropertyChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Slot != null)
        {
            Slot.PropertyChanged -= OnSlotPropertyChanged;
        }
    }

    private void OnSlotPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtworkSlotViewModel.ImageSource)
            or nameof(ArtworkSlotViewModel.ActualWidth)
            or nameof(ArtworkSlotViewModel.ActualHeight)
            or nameof(ArtworkSlotViewModel.HasImage))
        {
            Dispatcher.InvokeAsync(InvalidateMeasure);
        }
    }

    private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtworkViewportControl control)
        {
            if (e.OldValue is ArtworkSlotViewModel oldSlot)
            {
                oldSlot.PropertyChanged -= control.OnSlotPropertyChanged;
            }

            if (e.NewValue is ArtworkSlotViewModel slot)
            {
                slot.PropertyChanged += control.OnSlotPropertyChanged;

                if (control.AspectRatio <= 0)
                {
                    control.AspectRatio = slot.SlotType switch
                    {
                        SteamArtworkType.Capsule => 600.0 / 900.0,
                        SteamArtworkType.Hero => 1920.0 / 620.0,
                        SteamArtworkType.Header => 460.0 / 215.0,
                        SteamArtworkType.InstanceBanner => 460.0 / 215.0,
                        SteamArtworkType.Icon => 1.0,
                        SteamArtworkType.InstanceLogo => 1.0,
                        SteamArtworkType.Logo => 16.0 / 9.0,
                        _ => 0.0
                    };
                }

                if (slot.SlotType == SteamArtworkType.Logo)
                {
                    control.ShowCheckerboard = true;
                    control.StretchMode = Stretch.Uniform;
                }
            }

            control.InvalidateMeasure();
        }
    }

    private static void OnShowCheckerboardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtworkViewportControl control)
        {
            control.CheckerboardBackdrop.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (Slot?.IsDragOver == true) return;

        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160));
        HoverOverlay.BeginAnimation(OpacityProperty, anim);

        if (Slot?.HasImage == true)
        {
            ClearButton.Visibility = Visibility.Visible;
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160));
        HoverOverlay.BeginAnimation(OpacityProperty, anim);
        ClearButton.Visibility = Visibility.Collapsed;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == ClearButton || (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) == ClearButton))
        {
            return;
        }

        Slot?.ReplaceCommand.Execute(null);
    }

    private void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Slot?.ClearCommand.Execute(null);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (IsValidImageDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            if (Slot != null) Slot.IsDragOver = true;
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (IsValidImageDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            if (Slot != null) Slot.IsDragOver = true;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            if (Slot != null) Slot.IsDragOver = false;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (Slot != null) Slot.IsDragOver = false;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Slot != null) Slot.IsDragOver = false;

        if (IsValidImageDrop(e))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && Slot != null)
            {
                var filePath = files[0];
                var vm = FindInstanceDetailViewModel();
                if (vm != null)
                {
                    vm.HandleArtworkDrop(Slot.SlotType, filePath);
                }
            }
            e.Handled = true;
        }
    }

    private static bool IsValidImageDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) return false;

        var file = files[0];
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;

        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico" or ".bmp";
    }

    private InstanceDetailViewModel? FindInstanceDetailViewModel()
    {
        if (DataContext is InstanceDetailViewModel vm) return vm;

        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is InstanceDetailViewModel parentVm)
            {
                return parentVm;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double ar = AspectRatio;

        // If an image is loaded and valid dimensions exist, use the exact natural aspect ratio of the image
        // within reasonable bounds for the slot type so that the box stretches to fit the image
        // without vertical cropping and without black bars ("justo antes de que aparezcan las barras negras").
        if (Slot?.HasImage == true && Slot.ActualWidth > 0 && Slot.ActualHeight > 0)
        {
            double naturalAr = (double)Slot.ActualWidth / Slot.ActualHeight;
            if (Slot.SlotType == SteamArtworkType.Hero)
            {
                ar = Math.Clamp(naturalAr, 2.0, 4.0);
            }
            else if (Slot.SlotType is SteamArtworkType.Header or SteamArtworkType.InstanceBanner)
            {
                ar = Math.Clamp(naturalAr, 1.5, 3.0);
            }
            else if (ar <= 0)
            {
                ar = naturalAr;
            }
        }
        else if (ar <= 0 && Slot != null)
        {
            ar = Slot.SlotType switch
            {
                SteamArtworkType.Capsule => 600.0 / 900.0,
                SteamArtworkType.Hero => 1920.0 / 620.0,
                SteamArtworkType.Header => 460.0 / 215.0,
                SteamArtworkType.InstanceBanner => 460.0 / 215.0,
                SteamArtworkType.Icon => 1.0,
                SteamArtworkType.InstanceLogo => 1.0,
                SteamArtworkType.Logo => 16.0 / 9.0,
                _ => 0.0
            };
        }

        if (ar > 0)
        {
            bool hasExplicitW = !double.IsNaN(Width) && Width > 0;
            bool hasExplicitH = !double.IsNaN(Height) && Height > 0;

            if (hasExplicitW && hasExplicitH)
            {
                base.MeasureOverride(new Size(Width, Height));
                return new Size(Width, Height);
            }

            if (hasExplicitW)
            {
                double h = Width / ar;
                if (!double.IsNaN(MaxHeight) && h > MaxHeight) h = MaxHeight;
                base.MeasureOverride(new Size(Width, h));
                return new Size(Width, h);
            }

            if (hasExplicitH)
            {
                double w;
                if (HorizontalAlignment == HorizontalAlignment.Stretch && !double.IsInfinity(constraint.Width) && constraint.Width > 0)
                {
                    w = constraint.Width;
                }
                else
                {
                    w = Height * ar;
                    if (!double.IsInfinity(constraint.Width) && constraint.Width > 0 && w > constraint.Width)
                    {
                        w = constraint.Width;
                    }
                }
                if (!double.IsNaN(MaxWidth) && w > MaxWidth) w = MaxWidth;
                base.MeasureOverride(new Size(w, Height));
                return new Size(w, Height);
            }

            if (!double.IsInfinity(constraint.Width) && constraint.Width > 0)
            {
                double w = constraint.Width;
                if (!double.IsNaN(MaxWidth) && w > MaxWidth) w = MaxWidth;
                double h = w / ar;
                if (!double.IsNaN(MaxHeight) && h > MaxHeight)
                {
                    h = MaxHeight;
                }
                base.MeasureOverride(new Size(w, h));
                return new Size(w, h);
            }
        }

        return base.MeasureOverride(constraint);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.NewSize.Width > 0 && sizeInfo.NewSize.Height > 0)
        {
            ViewportBorder.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, sizeInfo.NewSize.Width, sizeInfo.NewSize.Height),
                RadiusX = 8,
                RadiusY = 8
            };
        }
    }
}
