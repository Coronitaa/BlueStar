using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Fast, deterministic text normalizer for Steam titles, aliases, and search terms.
/// Handles diacritic removal, punctuation stripping, whitespace collapsing,
/// and compact key generation (e.g. "counter-strike" -> "counter strike" and "counterstrike").
/// </summary>
public static partial class DeterministicNormalizer
{
    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]+", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultipleWhitespaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericRegex();

    /// <summary>
    /// Normalizes text into lower-case, unaccented, punctuation-free string separated by single spaces.
    /// E.g. "The Witcher® 3: Wild Hunt" -> "the witcher 3 wild hunt"
    /// E.g. "Counter-Strike: Global Offensive" -> "counter strike global offensive"
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Remove diacritics
        var unaccented = RemoveDiacritics(input);

        // 2. Replace hyphens and separators with spaces before removing other punctuation
        var spaced = unaccented.Replace('-', ' ')
                               .Replace('_', ' ')
                               .Replace('/', ' ')
                               .Replace('\\', ' ')
                               .Replace(':', ' ')
                               .Replace(';', ' ')
                               .Replace('.', ' ');

        // 3. Remove remaining punctuation / special symbols
        var cleaned = PunctuationRegex().Replace(spaced, " ");

        // 4. Collapse spaces and trim
        var collapsed = MultipleWhitespaceRegex().Replace(cleaned, " ").Trim();

        return collapsed.ToLowerInvariant();
    }

    /// <summary>
    /// Generates a compact alphanumeric representation without spaces or punctuation.
    /// E.g. "Cyber Punk" -> "cyberpunk", "Counter-Strike" -> "counterstrike"
    /// </summary>
    public static string ToCompactKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var unaccented = RemoveDiacritics(input);
        var compact = NonAlphanumericRegex().Replace(unaccented, string.Empty);
        return compact.ToLowerInvariant();
    }

    /// <summary>
    /// Decomposes Unicode characters into base glyphs + combining marks,
    /// then strips the non-spacing combining marks.
    /// </summary>
    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(normalizedString.Length);

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }
}
