using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueStar.App.Services;

namespace BlueStar.App.Controls;

/// <summary>
/// Loads an image URL off the UI thread into an <see cref="ImageBrush"/> or <see cref="Image"/>.
/// </summary>
/// <remarks>
/// <see cref="Converters.CachedImageConverter"/> reads and decodes cache hits on the UI thread and downloads
/// misses through BitmapImage's own downloader, so a page of new cards froze scrolling. Here only an in-memory
/// hit is applied synchronously; disk reads, downloads and decoding run in <see cref="ImageCacheService.LoadImageAsync"/>.
/// </remarks>
public static class AsyncImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject target) => (string?)target.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject target, string? value) => target.SetValue(UrlProperty, value);

    private static async void OnUrlChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
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

        var loaded = await cache.LoadImageAsync(url);

        // A recycled card may show another game by now: only the URL it still asks for is applied.
        if (loaded != null && string.Equals(GetUrl(target), url, System.StringComparison.Ordinal))
        {
            Apply(target, loaded);
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
