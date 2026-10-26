using System;
using System.Globalization;
using System.IO;
using BlueStar.App.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.App.Tests;

public class SystemLanguageTests
{
    [Theory]
    [InlineData("es-AR", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("es-419", "es")]
    [InlineData("en-US", "en")]
    [InlineData("pt-BR", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("ja-JP", "en")]
    public void First_launch_uses_Spanish_for_Spanish_Windows_and_English_otherwise(string culture, string expected)
    {
        LocalizationService.ResolveSystemLanguage(new CultureInfo(culture)).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ \"language\": \"system\" }")]
    public void Settings_without_a_chosen_language_report_none(string? json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            if (json != null) File.WriteAllText(path, json);
            var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, path);

            (settings.ConfiguredLanguage is null or "system").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
