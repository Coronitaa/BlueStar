using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueStar.Core.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Represents a single Steam artwork slot (Capsule, Hero, Logo, Header, Icon)
/// displayed and editable within the instance Settings tab.
/// </summary>
public partial class ArtworkSlotViewModel : ObservableObject
{
    private readonly Action<SteamArtworkType> _onReplaceRequested;
    private readonly Action<SteamArtworkType> _onClearRequested;

    public SteamArtworkType SlotType { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string? _imagePath;

    [ObservableProperty]
    private ImageSource? _imageSource;

    [ObservableProperty]
    private string? _actualDimensions;

    [ObservableProperty]
    private int _actualWidth;

    [ObservableProperty]
    private int _actualHeight;

    [ObservableProperty]
    private bool _hasImage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDragOver;

    public ArtworkSlotViewModel(
        SteamArtworkType slotType,
        string title,
        string subtitle,
        Action<SteamArtworkType> onReplaceRequested,
        Action<SteamArtworkType> onClearRequested)
    {
        SlotType = slotType;
        Title = title;
        Subtitle = subtitle;
        _onReplaceRequested = onReplaceRequested;
        _onClearRequested = onClearRequested;
    }

    [RelayCommand]
    private void Replace()
    {
        _onReplaceRequested(SlotType);
    }

    [RelayCommand]
    private void Clear()
    {
        _onClearRequested(SlotType);
    }

    /// <summary>
    /// Loads image bytes into a frozen, non-locking BitmapSource.
    /// </summary>
    public void LoadFromFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            ImagePath = null;
            ImageSource = null;
            ActualWidth = 0;
            ActualHeight = 0;
            ActualDimensions = null;
            HasImage = false;
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length > 0)
            {
                var (w, h) = InstanceArtworkService.GetImageDimensions(bytes);
                var (source, actualW, actualH) = DecodeImage(bytes, filePath, w, h);
                if (source != null)
                {
                    ImagePath = filePath;
                    ImageSource = source;
                    ActualWidth = actualW;
                    ActualHeight = actualH;
                    ActualDimensions = $"{ActualWidth} × {ActualHeight} px";
                    HasImage = true;
                    return;
                }
            }
        }
        catch { }

        ImagePath = null;
        ImageSource = null;
        ActualWidth = 0;
        ActualHeight = 0;
        ActualDimensions = null;
        HasImage = false;
    }

    /// <summary>
    /// Sets loaded image directly from bytes.
    /// </summary>
    public void LoadFromBytes(byte[] bytes, string? filePath = null)
    {
        if (bytes == null || bytes.Length == 0)
        {
            LoadFromFile(null);
            return;
        }

        try
        {
            var (w, h) = InstanceArtworkService.GetImageDimensions(bytes);
            var (source, actualW, actualH) = DecodeImage(bytes, filePath, w, h);
            if (source != null)
            {
                ImagePath = filePath;
                ImageSource = source;
                ActualWidth = actualW;
                ActualHeight = actualH;
                ActualDimensions = $"{ActualWidth} × {ActualHeight} px";
                HasImage = true;
                return;
            }
            LoadFromFile(null);
        }
        catch
        {
            LoadFromFile(null);
        }
    }
    private static (ImageSource? Source, int Width, int Height) DecodeImage(byte[] bytes, string? filePath, int fallbackW, int fallbackH)
    {
        if (bytes == null || bytes.Length == 0) return (null, 0, 0);

        int w = fallbackW;
        int h = fallbackH;

        // For ICO files, inspect all frames and pick the highest resolution one (e.g. 256x256)
        if (filePath?.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) == true ||
            (bytes.Length > 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0))
        {
            try
            {
                using var icoMs = new MemoryStream(bytes);
                var decoder = new IconBitmapDecoder(icoMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? bestFrame = null;
                int maxPixels = -1;
                foreach (var frame in decoder.Frames)
                {
                    int p = frame.PixelWidth * frame.PixelHeight;
                    if (p > maxPixels)
                    {
                        maxPixels = p;
                        bestFrame = frame;
                    }
                }
                if (bestFrame != null)
                {
                    if (bestFrame.CanFreeze) bestFrame.Freeze();
                    w = bestFrame.PixelWidth;
                    h = bestFrame.PixelHeight;
                    return (bestFrame, w, h);
                }
            }
            catch { }
        }

        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.StreamSource = ms;
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return (bmp, w > 0 ? w : bmp.PixelWidth, h > 0 ? h : bmp.PixelHeight);
    }
}
