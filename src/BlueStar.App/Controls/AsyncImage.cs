using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueStar.App.Services;

namespace BlueStar.App.Controls;

/// <summary>
/// Loads an image URL off the UI thread into an <see cref="ImageBrush"/> or <see cref="Image"/>.
/// Supports automatic cancellation when visuals are virtualized/recycled.
/// </summary>
public static class AsyncImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject target) => (string?)target.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject target, string? value) => target.SetValue(UrlProperty, value);

    private static readonly DependencyProperty CancellationTokenSourceProperty = DependencyProperty.RegisterAttached(
        "CancellationTokenSource", typeof(CancellationTokenSource), typeof(AsyncImage));

    private static CancellationTokenSource? GetCts(DependencyObject target) => (CancellationTokenSource?)target.GetValue(CancellationTokenSourceProperty);

    private static void SetCts(DependencyObject target, CancellationTokenSource? value) => target.SetValue(CancellationTokenSourceProperty, value);

    private static async void OnUrlChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        // Cancel any pending load for a recycled visual
        var oldCts = GetCts(target);
        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
            SetCts(target, null);
        }

        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            Apply(target, null);
            return;
        }

        var cache = ImageCacheService.Instance;
        var cached = cache.GetFromMemory(url);
        Apply(target, cached);
        if (cached != null) return;

        var cts = new CancellationTokenSource();
        SetCts(target, cts);

        try
        {
            var loaded = await cache.LoadImageAsync(url, 0, null, cts.Token);

            if (!cts.IsCancellationRequested &&
                loaded != null &&
                string.Equals(GetUrl(target), url, StringComparison.Ordinal))
            {
                Apply(target, loaded);
            }
        }
        catch (OperationCanceledException)
        {
            // Visual recycled, ignore
        }
        finally
        {
            if (ReferenceEquals(GetCts(target), cts))
            {
                SetCts(target, null);
                cts.Dispose();
            }
        }
    }

    private static void Apply(DependencyObject target, ImageSource? source)
    {
        switch (target)
        {
            case ImageBrush brush:
                brush.ImageSource = source;
                break;
            case Image image:
                image.Source = source;
                break;
        }
    }
}
