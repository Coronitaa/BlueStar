using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.Services;

/// <summary>
/// Service managing dynamic runtime string localizations and ResourceDictionary swapping for BlueStar.
/// </summary>
public sealed class LocalizationService : ILocalizationService, INotifyPropertyChanged
{
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<LocalizationService> _logger;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    private string _currentLanguage = "en";
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentLanguageDisplayName));
            }
        }
    }

    public string CurrentLanguageDisplayName =>
        SupportedLanguages.TryGetValue(CurrentLanguage, out var name) ? name : "English";

    public IReadOnlyDictionary<string, string> SupportedLanguages { get; } = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["es"] = "Español (Latinoamérica)"
    };

    public LocalizationService(AppSettingsService settingsService, ILogger<LocalizationService> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var initialLang = _settingsService.Language;
        if (string.IsNullOrWhiteSpace(initialLang) || !SupportedLanguages.ContainsKey(initialLang))
        {
            initialLang = "en";
        }

        SetLanguage(initialLang, persist: false);
    }

    public void SetLanguage(string languageCode) => SetLanguage(languageCode, persist: true);

    private void SetLanguage(string languageCode, bool persist)
    {
        var normalized = languageCode?.ToLowerInvariant().StartsWith("es") == true ? "es" : "en";

        try
        {
            var dictUri = new Uri($"pack://application:,,,/BlueStar;component/Themes/Strings.{normalized}.xaml", UriKind.Absolute);
            var newDict = new ResourceDictionary { Source = dictUri };

            var app = Application.Current;
            if (app != null)
            {
                var merged = app.Resources.MergedDictionaries;
                var existingIndex = -1;

                for (int i = 0; i < merged.Count; i++)
                {
                    var src = merged[i].Source?.ToString();
                    if (src != null && src.Contains("Themes/Strings."))
                    {
                        existingIndex = i;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    merged[existingIndex] = newDict;
                }
                else
                {
                    merged.Add(newDict);
                }
            }

            var culture = normalized == "es" ? new CultureInfo("es-419") : new CultureInfo("en-US");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            CurrentLanguage = normalized;

            if (persist)
            {
                _ = _settingsService.SetLanguageAsync(normalized);
            }

            LanguageChanged?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Application language switched to {Language}", normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply language {Language}", languageCode);
        }
    }

    public string GetString(string key, string? fallback = null)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is string val)
            {
                return val;
            }
        }
        catch { }

        return fallback ?? key;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
