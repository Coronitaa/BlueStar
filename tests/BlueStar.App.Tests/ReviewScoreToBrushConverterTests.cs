using System.Globalization;
using System.Windows.Media;
using BlueStar.App.Converters;
using Xunit;

namespace BlueStar.App.Tests;

public class ReviewScoreToBrushConverterTests
{
    private readonly ReviewScoreToBrushConverter _converter = new();

    [Theory]
    [InlineData(100, 102, 192, 244)] // #66C0F4 (Positive)
    [InlineData(70, 102, 192, 244)]  // #66C0F4 (Positive boundary)
    [InlineData(69, 185, 160, 116)]  // #B9A074 (Mixed boundary)
    [InlineData(40, 185, 160, 116)]  // #B9A074 (Mixed)
    [InlineData(39, 195, 92, 44)]    // #C35C2C (Negative boundary)
    [InlineData(1, 195, 92, 44)]     // #C35C2C (Negative)
    [InlineData(0, 143, 152, 160)]   // #8F98A0 (Unrated)
    public void Convert_IntScore_ReturnsExpectedSteamColor(int percent, byte r, byte g, byte b)
    {
        var result = _converter.Convert(percent, typeof(SolidColorBrush), null!, CultureInfo.InvariantCulture);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(r, g, b), brush.Color);
    }

    [Theory]
    [InlineData("Very Positive (92%)", 102, 192, 244)]
    [InlineData("Mixed (55%)", 185, 160, 116)]
    [InlineData("Mostly Negative (32%)", 195, 92, 44)]
    public void Convert_StringSummary_ReturnsExpectedSteamColor(string summary, byte r, byte g, byte b)
    {
        var result = _converter.Convert(summary, typeof(SolidColorBrush), null!, CultureInfo.InvariantCulture);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(r, g, b), brush.Color);
    }
}
