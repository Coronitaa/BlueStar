using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueStar.App.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.App.Tests;

public class IconConversionHelperTests
{
    [Fact]
    public void IsValidIco_RecognizesValidAndInvalidHeaders()
    {
        IconConversionHelper.IsValidIco(null).Should().BeFalse();
        IconConversionHelper.IsValidIco(new byte[10]).Should().BeFalse();

        // Valid ICO header: 00 00 01 00 01 00 + 16 bytes directory
        var validHeader = new byte[22];
        validHeader[2] = 1; // type = 1
        validHeader[4] = 1; // count = 1
        IconConversionHelper.IsValidIco(validHeader).Should().BeTrue();

        // Non-ico
        validHeader[2] = 2; // type = 2 (CUR)
        IconConversionHelper.IsValidIco(validHeader).Should().BeFalse();
    }

    [Fact]
    public void ConvertToWindowsIco_ConvertsPngToMultiResolutionIco()
    {
        StaTestHelper.Run(() =>
        {
            // 1. Create a dynamic test PNG bitmap (100x100)
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.DodgerBlue, null, new Rect(0, 0, 100, 100));
                dc.DrawEllipse(Brushes.White, null, new Point(50, 50), 25, 25);
            }

            var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var pngMs = new MemoryStream();
            encoder.Save(pngMs);
            var pngBytes = pngMs.ToArray();

            // 2. Convert to ICO
            var icoBytes = IconConversionHelper.ConvertToWindowsIco(pngBytes);
            icoBytes.Should().NotBeNull();
            icoBytes.Length.Should().BeGreaterThan(pngBytes.Length);
            IconConversionHelper.IsValidIco(icoBytes).Should().BeTrue();

            // 3. Decode the generated ICO and verify all 4 standard frames are present (256, 48, 32, 16)
            using var icoMs = new MemoryStream(icoBytes);
            var decoder = new IconBitmapDecoder(icoMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            decoder.Frames.Count.Should().Be(4);

            var sizes = new System.Collections.Generic.HashSet<int>();
            foreach (var frame in decoder.Frames)
            {
                sizes.Add(frame.PixelWidth);
                frame.PixelWidth.Should().Be(frame.PixelHeight); // square
            }

            sizes.Should().Contain([256, 48, 32, 16]);

            // 4. Extract PNG from ICO
            var extractedPng = IconConversionHelper.ExtractPngFromIco(icoBytes, 256);
            extractedPng.Should().NotBeNull();
            extractedPng!.Length.Should().BeGreaterThan(0);
            extractedPng[0].Should().Be(0x89);
            extractedPng[1].Should().Be(0x50);
            extractedPng[2].Should().Be(0x4E);
            extractedPng[3].Should().Be(0x47);
        });
    }
}
