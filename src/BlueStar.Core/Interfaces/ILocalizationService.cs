using System;
using System.Collections.Generic;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service contract for managing application language and runtime string localizations.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Current active language code (e.g. en, es).
    /// </summary>
    string CurrentLanguage { get; }

    /// <summary>
    /// Supported language options (e.g. en -> English, es -> Español (Latinoamérica)).
    /// </summary>
    IReadOnlyDictionary<string, string> SupportedLanguages { get; }

    /// <summary>
    /// Event fired whenever the application language changes.
    /// </summary>
    event EventHandler? LanguageChanged;

    /// <summary>
    /// Switches the application language at runtime.
    /// </summary>
    /// <param name=languageCode>Target language code (en or es).</param>
    void SetLanguage(string languageCode);

    /// <summary>
    /// Resolves a localized string key from the active dictionary.
    /// </summary>
    /// <param name=key>Resource key name (e.g. String_Dashboard).</param>
    /// <param name=fallback>Optional fallback if key is not found.</param>
    string GetString(string key, string? fallback = null);
}
