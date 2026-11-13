using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueStar.App.Services;

namespace BlueStar.App.Controls;

/// <summary>
/// Loads an image URL off the UI thread into an <see cref="ImageBrush"/> or <see cref="Image"/>.
/// Supports cooperative cancellation when visuals are virtualized/recycled.
/// </summary>
public static class AsyncImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject target) => (string?)target.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject target, string? value) => target.SetValue(UrlProperty, value);

    public static readonly DependencyProperty AppIdProperty = DependencyProperty.RegisterAttached(
        "AppId", typeof(uint), typeof(AsyncImage), new PropertyMetadata(0u, OnAppIdChanged));

    public static uint GetAppId(DependencyObject target) => (uint)target.GetValue(AppIdProperty);

    public static void SetAppId(DependencyObject target, uint value) => target.SetValue(AppIdProperty, value);

    private static readonly DependencyProperty CancellationTokenSourceProperty = DependencyProperty.RegisterAttached(
        "CancellationTokenSource", typeof(CancellationTokenSource), typeof(AsyncImage));

    private static CancellationTokenSource? GetCts(DependencyObject target) => (CancellationTokenSource?)target.GetValue(CancellationTokenSourceProperty);

    private static void SetCts(DependencyObject target, CancellationTokenSource? value) => target.SetValue(CancellationTokenSourceProperty, value);

    private static void OnUrlChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var url = e.NewValue as string;
        var appId = GetAppId(target);
        Reload(target, url, appId);
    }

    private static void OnAppIdChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var appId = (uint)e.NewValue;
        var url = GetUrl(target);
        if (string.IsNullOrWhiteSpace(url) && appId > 0)
        {
            Reload(target, null, appId);
        }
    }

    private static async void Reload(DependencyObject target, string? url, uint appId)
    {
        var oldCts = GetCts(target);
        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
            SetCts(target, null);
        }

        if (string.IsNullOrWhiteSpace(url) && appId == 0)
        {
            Apply(target, null);
            return;
        }

        var cache = ImageCacheService.Instance;
        var cached = cache.GetFromMemory(url, appId);
        Apply(target, cached);
        if (cached != null) return;

        var cts = new CancellationTokenSource();
        SetCts(target, cts);

        try
        {
            var loaded = await cache.LoadImageAsync(url, appId, null, cts.Token);

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
