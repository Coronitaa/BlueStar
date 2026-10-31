using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class GameNameSanitizerTests
{
    [Theory]
    [InlineData("MECCHA CHAMELEON PRUEBITAS", 1818750, "MECCHA CHAMELEON")]
    [InlineData("GRAIN ROT PRUEBAS", 12345, "GRAIN ROT")]
    [InlineData("Stardew Valley (Pruebas)", 413150, "Stardew Valley")]
    [InlineData("Cyberpunk 2077 - Copy", 1091500, "Cyberpunk 2077")]
    [InlineData("The Witcher 3: Wild Hunt (Test 1)", 292030, "The Witcher 3: Wild Hunt")]
    [InlineData("Doom [Instance 2]", 379720, "Doom")]
    [InlineData("Hades II", 1145360, "Hades II")]
    [InlineData("Elden Ring - Copia", 1245620, "Elden Ring")]
    [InlineData("Counter-Strike 2", 730, "Counter-Strike 2")]
    public void Sanitize_RemovesInstanceAndTestTags(string rawName, uint appId, string expected)
    {
        var result = GameNameSanitizer.Sanitize(rawName, appId);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("PRUEBAS", 730, "Game 730")]
    [InlineData("pruebitas", 123, "Game 123")]
    [InlineData("test", 456, "Game 456")]
    [InlineData("", 789, "Game 789")]
    [InlineData("   ", 0, "Game")]
    public void Sanitize_NoiseOrEmpty_FallsBackGracefully(string rawName, uint appId, string expected)
    {
        var result = GameNameSanitizer.Sanitize(rawName, appId);
        result.Should().Be(expected);
    }
}
