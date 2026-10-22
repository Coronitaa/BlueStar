using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueStar.App.Helpers;

/// <summary>
/// Helper to convert any image format (PNG, JPEG, WEBP, BMP, GIF, etc.) into a 100% compliant,
/// multi-resolution Windows .ico container (256x256, 48x48, 32x32, 16x16) suitable for Windows
/// shell shortcuts (.lnk), Desktop, Start Menu, Taskbar, and Steam shortcuts.
/// </summary>
public static class IconConversionHelper
{
    private static readonly int[] StandardIconSizes = [256, 48, 32, 16];

    /// <summary>
    /// Checks whether the byte array starts with a valid Windows ICO header.
    /// </summary>
    public static bool IsValidIco(byte[]? data)
    {
        if (data == null || data.Length < 22) return false;
        // Reserved (00 00) + Type (01 00 for ICO) + Count (>= 1)
        return data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0 && (data[4] > 0 || data[5] > 0);
    }

    /// <summary>
    /// Converts any image byte array (PNG, JPG, BMP, WEBP, etc.) into a valid Windows .ico file.
    /// If the data is already a valid .ico, it returns the original bytes untouched.
    /// </summary>
    public static byte[] ConvertToWindowsIco(byte[] inputBytes)
    {
        if (inputBytes == null || inputBytes.Length == 0)
        {
            return inputBytes ?? [];
        }

        if (IsValidIco(inputBytes))
        {
            return inputBytes;
        }

        try
        {
            BitmapSource? source = DecodeToBitmapSource(inputBytes);
            if (source == null)
            {
                return inputBytes;
            }

            // Ensure source is 1:1 square by center cropping
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            if (w != h && w > 0 && h > 0)
            {
                int squareSize = Math.Min(w, h);
                int cropX = (w - squareSize) / 2;
                int cropY = (h - squareSize) / 2;
                source = new CroppedBitmap(source, new Int32Rect(cropX, cropY, squareSize, squareSize));
            }

            // Convert to 32-bit BGRA to ensure consistent color format and alpha channel
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            if (formatted.CanFreeze) formatted.Freeze();

            var frames = new List<(int Size, byte[] PngBytes)>();
            foreach (var size in StandardIconSizes)
            {
                var pngBytes = RenderPngFrame(formatted, size);
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    frames.Add((size, pngBytes));
                }
            }

            if (frames.Count == 0)
            {
                return inputBytes;
            }

            return PackIcoFrames(frames);
        }
        catch
        {
            return inputBytes;
        }
    }

    /// <summary>
    /// Decodes the largest/highest-resolution frame from a Windows ICO file into a PNG byte array.
    /// </summary>
    public static byte[]? ExtractPngFromIco(byte[] icoBytes, int targetPreferredSize = 256)
    {
        if (icoBytes == null || icoBytes.Length < 22) return null;

        try
        {
            using var ms = new MemoryStream(icoBytes);
            var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame? bestFrame = null;
            int bestScore = -1;

            foreach (var frame in decoder.Frames)
            {
                int p = frame.PixelWidth * frame.PixelHeight;
                if (frame.PixelWidth == targetPreferredSize)
                {
                    bestFrame = frame;
                    break;
                }
                if (p > bestScore)
                {
                    bestScore = p;
                    bestFrame = frame;
                }
            }

            if (bestFrame != null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(bestFrame);
                using var outMs = new MemoryStream();
                encoder.Save(outMs);
                return outMs.ToArray();
            }
        }
        catch { }

        return null;
    }

    private static BitmapSource? DecodeToBitmapSource(byte[] inputBytes)
    {
        try
        {
            using var ms = new MemoryStream(inputBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }
        }
        catch { }

        try
        {
            using var ms = new MemoryStream(inputBytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            return bi;
        }
        catch { }

        return null;
    }

    private static byte[]? RenderPngFrame(BitmapSource source, int targetSize)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
                dc.DrawImage(source, new Rect(0, 0, targetSize, targetSize));
            }

            var rtb = new RenderTargetBitmap(targetSize, targetSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            if (rtb.CanFreeze) rtb.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] PackIcoFrames(List<(int Size, byte[] PngBytes)> frames)
    {
        using var icoStream = new MemoryStream();
        using var bw = new BinaryWriter(icoStream);

        // 1. ICONDIR Header (6 bytes)
        bw.Write((ushort)0);               // Reserved must be 0
        bw.Write((ushort)1);               // Resource Type: 1 = ICO
        bw.Write((ushort)frames.Count);     // Number of images

        // 2. ICONDIRENTRY array (16 bytes per entry)
        int headerSize = 6 + (16 * frames.Count);
        int currentOffset = headerSize;

        foreach (var (size, pngBytes) in frames)
        {
            bw.Write((byte)(size >= 256 ? 0 : size)); // Width (0 = 256)
            bw.Write((byte)(size >= 256 ? 0 : size)); // Height (0 = 256)
            bw.Write((byte)0);                        // Color count
            bw.Write((byte)0);                        // Reserved
            bw.Write((ushort)1);                      // Planes
            bw.Write((ushort)32);                     // Bits per pixel
            bw.Write((uint)pngBytes.Length);           // Image data length
            bw.Write((uint)currentOffset);             // Image data offset
            currentOffset += pngBytes.Length;
        }

        // 3. Image data blocks (PNG encoded)
        foreach (var (_, pngBytes) in frames)
        {
            bw.Write(pngBytes);
        }

        return icoStream.ToArray();
    }
}
