using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BlueStar.Core.Helpers;

namespace BlueStar.App.Helpers;

/// <summary>
/// Helper to crop artwork images to the target aspect ratio required by each Steam and instance artwork slot.
/// </summary>
public static class ImageCropHelper
{
    /// <summary>
    /// Gets the target aspect ratio (width / height) for a given artwork slot type.
    /// Returns 0 if the type has no fixed aspect ratio constraint (e.g. Logo).
    /// </summary>
    public static double GetTargetAspectRatio(SteamArtworkType type) => type switch
    {
        SteamArtworkType.Capsule => 600.0 / 900.0,            // 2:3 (~0.6667)
        SteamArtworkType.Hero => 1920.0 / 620.0,              // ~3.0968
        SteamArtworkType.Header => 460.0 / 215.0,             // ~2.1395
        SteamArtworkType.InstanceBanner => 460.0 / 215.0,     // ~2.1395
        SteamArtworkType.Icon => 1.0,                         // 1:1
        SteamArtworkType.InstanceLogo => 1.0,                 // 1:1
        _ => 0.0
    };

    /// <summary>
    /// Crops the image bytes to match the target aspect ratio by centering the crop rectangle.
    /// If the image already matches the target aspect ratio (within 1.5% tolerance),
    /// the original bytes and dimensions are returned without re-encoding.
    /// </summary>
    public static (byte[] Data, int Width, int Height, string Extension) CropToAspectRatio(
        byte[] inputBytes,
        double targetRatio,
        string originalExtension)
    {
        if (inputBytes == null || inputBytes.Length == 0 || targetRatio <= 0)
        {
            return (inputBytes ?? [], 0, 0, originalExtension);
        }

        try
        {
            using var inStream = new MemoryStream(inputBytes);
            var decoder = BitmapDecoder.Create(
                inStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return (inputBytes, 0, 0, originalExtension);
            }

            var frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;

            if (w <= 0 || h <= 0)
            {
                return (inputBytes, w, h, originalExtension);
            }

            double currentRatio = (double)w / h;
            // If already within 1.5% tolerance of target aspect ratio, no crop needed
            if (Math.Abs(currentRatio - targetRatio) / targetRatio < 0.015)
            {
                return (inputBytes, w, h, originalExtension);
            }

            int cropX, cropY, cropW, cropH;
            if (currentRatio > targetRatio)
            {
                // Image is wider than target ratio: crop width horizontally centered
                cropW = Math.Clamp((int)Math.Round(h * targetRatio), 1, w);
                cropH = h;
                cropX = (w - cropW) / 2;
                cropY = 0;
            }
            else
            {
                // Image is taller than target ratio: crop height vertically centered
                cropW = w;
                cropH = Math.Clamp((int)Math.Round(w / targetRatio), 1, h);
                cropX = 0;
                cropY = (h - cropH) / 2;
            }

            var croppedBitmap = new CroppedBitmap(frame, new Int32Rect(cropX, cropY, cropW, cropH));
            if (croppedBitmap.CanFreeze) croppedBitmap.Freeze();

            var ext = originalExtension.ToLowerInvariant();
            if (!ext.StartsWith('.')) ext = "." + ext;

            BitmapEncoder encoder;
            string outExt;
            if (ext is ".png" or ".ico")
            {
                encoder = new PngBitmapEncoder();
                outExt = ".png";
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = 94 };
                outExt = ".jpg";
            }

            encoder.Frames.Add(BitmapFrame.Create(croppedBitmap));
            using var outStream = new MemoryStream();
            encoder.Save(outStream);

            return (outStream.ToArray(), cropW, cropH, outExt);
        }
        catch
        {
            // If decoding or cropping fails (e.g. unsupported format), return original
            return (inputBytes, 0, 0, originalExtension);
        }
    }
}
