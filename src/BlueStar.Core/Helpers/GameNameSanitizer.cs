using System;
using System.Text.RegularExpressions;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Utility to clean and sanitize game names, ensuring community stats and trending suggestions
/// represent canonical game titles rather than user instance tags (e.g., 'PRUEBAS', 'TEST', 'Copy').
/// </summary>
public static class GameNameSanitizer
{
    private static readonly Regex TrailingJunkRegex = new(
        @"(?i)(?:\s*[-_–—:]*\s*(?:pruebitas?|pruebas?|tests?|testing|copia|copy|instancias?|instances?|debug|build)\s*\d*|\s*[\(\[]\s*\d+\s*[\)\]])+$",
        RegexOptions.Compiled);

    private static readonly Regex ParenthesizedJunkRegex = new(
        @"(?i)\s*[\(\[]\s*(?:pruebitas?|pruebas?|tests?|testing|copia|copy|debug|build|instancias?|instances?)\s*\d*\s*[\)\]]",
        RegexOptions.Compiled);

    private static readonly Regex MultipleSpacesRegex = new(
        @"\s{2,}",
        RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a raw game or instance name into a canonical game title candidate.
    /// </summary>
    public static string Sanitize(string? rawName, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return appId > 0 ? $"Game {appId}" : "Game";
        }

        var cleaned = rawName.Trim();
        cleaned = ParenthesizedJunkRegex.Replace(cleaned, "");
        cleaned = TrailingJunkRegex.Replace(cleaned, "");
        cleaned = MultipleSpacesRegex.Replace(cleaned, " ");
        cleaned = cleaned.Trim(' ', '-', '_', ':', '–', '—');

        if (string.IsNullOrWhiteSpace(cleaned) ||
            cleaned.Equals("prueba", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("pruebas", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("pruebita", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("pruebitas", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("testing", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("copy", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("copia", StringComparison.OrdinalIgnoreCase))
        {
            return appId > 0 ? $"Game {appId}" : "Game";
        }

        return cleaned;
    }
}
