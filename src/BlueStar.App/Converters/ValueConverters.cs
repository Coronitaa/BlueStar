using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;

namespace BlueStar.App.Converters;

/// <summary>
/// Converts a boolean to <see cref="Visibility"/>. True = Visible, False = Collapsed.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// Inverts a boolean value.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>
/// Returns Visible when the value is not null and not empty, Collapsed otherwise.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s) return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
        if (value is int count) return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Collapsed when the value is not null and not empty, Visible when null or empty.
/// </summary>
public sealed class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Visible;
        if (value is string s) return string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
        if (value is int count) return count > 0 ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a non-null/non-empty value to true, and null/empty to false.
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return false;
        if (value is string s) return !string.IsNullOrWhiteSpace(s);
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the int value is 0, Collapsed otherwise.
/// </summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// False = Visible, True = Collapsed (inverse of BoolToVisibility).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// Checks whether a string matches the ConverterParameter string (returns bool).
/// </summary>
public sealed class StringMatchConverter : IValueConverter
{
    public static readonly StringMatchConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return true;
        if (value == null || parameter == null) return false;
        var pStr = parameter.ToString()?.Replace("\\,", ",");
        return string.Equals(value.ToString(), pStr, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string str) return str;
        return Binding.DoNothing;
    }
}

/// <summary>
/// Checks whether a string matches the ConverterParameter string and returns <see cref="Visibility"/>.
/// </summary>
/// <summary>
/// Maps an instance-detail tab key ("Overview", "Files", …) to the full localized section name
/// shown in the breadcrumb above the content. The navigation rail only has room for a short
/// label, so the breadcrumb is where the section gets named in full.
/// </summary>
/// <summary>
/// Turns a 0-100 percentage into a star <see cref="GridLength"/>, so a Grid can act as a
/// proportional bar without any code-behind: each segment claims exactly its share of the row.
/// A zero or negative value yields a zero-width column, which simply disappears.
/// </summary>
public sealed class PercentToStarLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0
        };

        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0)
            return new GridLength(0, GridUnitType.Star);

        return new GridLength(percent, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TabNameConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Overview"]      = "String_TabOverview",
        ["Files"]         = "String_TabVersion",
        ["Dlcs"]          = "String_TabDlcs",
        ["Mods"]          = "String_TabMods",
        ["Emulator"]      = "String_TabEmulator",
        ["Prerequisites"] = "String_TabPrerequisites",
        ["Settings"]      = "String_TabSettings",
        ["Logs"]          = "String_TabLogs",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tab = value as string;
        if (string.IsNullOrWhiteSpace(tab)) return string.Empty;

        if (!Keys.TryGetValue(tab, out var resourceKey)) return tab;

        try
        {
            var text = System.Windows.Application.Current?.TryFindResource(resourceKey) as string;
            return string.IsNullOrWhiteSpace(text) ? tab : text;
        }
        catch
        {
            return tab;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringMatchToVisibilityConverter : IValueConverter
{
    public static readonly StringMatchToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return Visibility.Visible;
        if (value == null || parameter == null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="DownloadJobStatus"/> → foreground SolidColorBrush.
/// </summary>
public sealed class DownloadStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x1B, 0x90, 0xFF));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DownloadJobStatus status) return Gray;
        return status switch
        {
            DownloadJobStatus.Completed   => Green,
            DownloadJobStatus.Failed      => Red,
            DownloadJobStatus.Canceled    => Amber,
            DownloadJobStatus.Downloading => Blue,
            DownloadJobStatus.Paused      => Purple,
            _                             => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="DownloadJobStatus"/> → subtle background SolidColorBrush for log rows.
/// </summary>
public sealed class DownloadStatusToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(0x20, 0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush RedBg = new(Color.FromArgb(0x25, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush AmberBg = new(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush BlueBg = new(Color.FromArgb(0x20, 0x1B, 0x90, 0xFF));
    private static readonly SolidColorBrush PurpleBg = new(Color.FromArgb(0x20, 0xA8, 0x55, 0xF7));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DownloadJobStatus status) return Brushes.Transparent;
        return status switch
        {
            DownloadJobStatus.Completed   => GreenBg,
            DownloadJobStatus.Failed      => RedBg,
            DownloadJobStatus.Canceled    => AmberBg,
            DownloadJobStatus.Downloading => BlueBg,
            DownloadJobStatus.Paused      => PurpleBg,
            _                             => Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="InstanceStatus"/> → foreground color brush.
/// </summary>
public sealed class InstanceStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush Cyan = new(Color.FromRgb(0x06, 0xB6, 0xD4));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x1B, 0x90, 0xFF));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not InstanceStatus status) return Gray;
        return status switch
        {
            InstanceStatus.Ready        => Green,
            InstanceStatus.Running      => Cyan,
            InstanceStatus.Downloading  => Blue,
            InstanceStatus.Updating     => Purple,
            InstanceStatus.Error        => Red,
            _                           => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="InstanceStatus"/> → badge background color brush.
/// </summary>
public sealed class InstanceStatusToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(0x22, 0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush CyanBg = new(Color.FromArgb(0x25, 0x06, 0xB6, 0xD4));
    private static readonly SolidColorBrush BlueBg = new(Color.FromArgb(0x22, 0x1B, 0x90, 0xFF));
    private static readonly SolidColorBrush PurpleBg = new(Color.FromArgb(0x22, 0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush RedBg = new(Color.FromArgb(0x22, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush GrayBg = new(Color.FromArgb(0x18, 0xA1, 0xA1, 0xA1));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not InstanceStatus status) return GrayBg;
        return status switch
        {
            InstanceStatus.Ready        => GreenBg,
            InstanceStatus.Running      => CyanBg,
            InstanceStatus.Downloading  => BlueBg,
            InstanceStatus.Updating     => PurpleBg,
            InstanceStatus.Error        => RedBg,
            _                           => GrayBg
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps Engine info to badge border/foreground brush.
/// </summary>
public sealed class EngineTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x1B, 0x90, 0xFF));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush Cyan = new(Color.FromRgb(0x06, 0xB6, 0xD4));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not EngineInfo engine) return Gray;
        return engine.Type switch
        {
            EngineType.UnrealEngine       => Blue,
            EngineType.Unity              => Purple,
            EngineType.Godot              => Cyan,
            EngineType.Source             => Amber,
            EngineType.Source2            => Amber,
            EngineType.Klei               => Green,
            EngineType.CreationEngine     => Amber,
            EngineType.ParadoxClausewitz  => Blue,
            EngineType.ReEngine           => Red,
            EngineType.MtFramework        => Red,
            EngineType.RedEngine          => Red,
            EngineType.Frostbite          => Cyan,
            EngineType.Decima             => Blue,
            EngineType.IdTech             => Red,
            EngineType.CryEngine          => Cyan,
            EngineType.Supergiant         => Red,
            EngineType.RpgMaker           => Purple,
            EngineType.RenPy              => Amber,
            EngineType.GameMaker          => Green,
            _                             => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts percentage 0-100 to sweep angle 0-360 for a circular arc.
/// </summary>
public sealed class PercentToAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? d / 100.0 * 360.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Text for DLC Unlock toggle button.
/// </summary>
public sealed class DlcUnlockButtonTextConverter : IValueConverter
{
    public static readonly DlcUnlockButtonTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "🔓 Remove DLC Unlocker" : "🔒 Install DLC Unlocker (SmokeAPI)";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Formats playtime TimeSpan to friendly string (e.g. "2h 45m" or "Never played").
/// </summary>
public sealed class PlayTimeToFormattedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts || ts == TimeSpan.Zero)
            return "Never played";

        if (ts.TotalHours >= 1)
            return $"{ts.TotalHours:F0}h {ts.Minutes}m";

        return $"{ts.Minutes}m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts NotificationType to accent color brush.
/// </summary>
public sealed class NotificationTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x1B, 0xD9, 0x6A));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NotificationType type) return Blue;
        return type switch
        {
            NotificationType.Success => Green,
            NotificationType.Info    => Blue,
            NotificationType.Warning => Amber,
            NotificationType.Error   => Red,
            NotificationType.Progress => Green,
            _ => Blue
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts NotificationType to subtle background tint.
/// </summary>
public sealed class NotificationTypeToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(0x22, 0x1B, 0xD9, 0x6A));
    private static readonly SolidColorBrush BlueBg = new(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush AmberBg = new(Color.FromArgb(0x22, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush RedBg = new(Color.FromArgb(0x22, 0xEF, 0x44, 0x44));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NotificationType type) return BlueBg;
        return type switch
        {
            NotificationType.Success => GreenBg,
            NotificationType.Info    => BlueBg,
            NotificationType.Warning => AmberBg,
            NotificationType.Error   => RedBg,
            NotificationType.Progress => GreenBg,
            _ => BlueBg
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts NotificationType to an emoji / text symbol.
/// </summary>
public sealed class NotificationTypeToIconTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NotificationType type) return "ℹ";
        return type switch
        {
            NotificationType.Success => "✓",
            NotificationType.Info    => "ℹ",
            NotificationType.Warning => "⚠",
            NotificationType.Error   => "✕",
            NotificationType.Progress => "⏳",
            _ => "ℹ"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible if the game instance has an outdated emulator installed compared to the launcher suite.
/// </summary>
public sealed class InstanceEmulatorUpdateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameInstance instance) return Visibility.Collapsed;
        if (!instance.EmulatorEnabled && string.IsNullOrWhiteSpace(instance.InstalledEmulatorVersion))
            return Visibility.Collapsed;

        var current = BlueStar.Infrastructure.Emulators.ReFixEmulator.GetCurrentVersion();
        var installed = instance.InstalledEmulatorVersion;
        if (string.IsNullOrWhiteSpace(installed))
            return instance.EmulatorEnabled ? Visibility.Visible : Visibility.Collapsed;

        return IsNewer(current, installed) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsNewer(string current, string installed)
    {
        current = current.TrimStart('v');
        installed = installed.TrimStart('v');
        if (Version.TryParse(current, out var cv) && Version.TryParse(installed, out var iv))
            return cv > iv;
        return string.Compare(current, installed, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Formats the tag text for outdated emulator on instance cards (e.g. "⚡ EMU UPDATE" or "⚡ EMU v1.0 ➜ v1.1").
/// </summary>
public sealed class InstanceEmulatorUpdateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameInstance instance) return "⚡ EMU UPDATE";
        var current = BlueStar.Infrastructure.Emulators.ReFixEmulator.GetCurrentVersion();
        var installed = instance.InstalledEmulatorVersion ?? "1.0";
        return $"⚡ EMU v{installed} ➜ v{current}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts PrerequisiteStatus to display text in Spanish/English.
/// </summary>
public sealed class PrerequisiteStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PrerequisiteStatus status) return "Unknown";
        return status switch
        {
            PrerequisiteStatus.InstalledInSystem => "✓ Installed in system",
            PrerequisiteStatus.InstalledSuccess => "✓ Installed successfully",
            PrerequisiteStatus.AvailableInGame => "📦 Found in game folder",
            PrerequisiteStatus.NeedsDownload => "🌐 Microsoft official download",
            PrerequisiteStatus.Installing => "⏳ Installing...",
            PrerequisiteStatus.InstallFailed => "❌ Installation failed",
            _ => status.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts PrerequisiteStatus to foreground text Brush.
/// </summary>
public sealed class PrerequisiteStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(168, 85, 247));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(161, 161, 170));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PrerequisiteStatus status) return Gray;
        return status switch
        {
            PrerequisiteStatus.InstalledInSystem or PrerequisiteStatus.InstalledSuccess => Green,
            PrerequisiteStatus.AvailableInGame => Amber,
            PrerequisiteStatus.NeedsDownload => Blue,
            PrerequisiteStatus.Installing => Purple,
            PrerequisiteStatus.InstallFailed => Red,
            _ => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts PrerequisiteStatus to background pill Brush.
/// </summary>
public sealed class PrerequisiteStatusToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(0x28, 34, 197, 94));
    private static readonly SolidColorBrush AmberBg = new(Color.FromArgb(0x28, 245, 158, 11));
    private static readonly SolidColorBrush BlueBg = new(Color.FromArgb(0x28, 59, 130, 246));
    private static readonly SolidColorBrush PurpleBg = new(Color.FromArgb(0x28, 168, 85, 247));
    private static readonly SolidColorBrush RedBg = new(Color.FromArgb(0x28, 239, 68, 68));
    private static readonly SolidColorBrush GrayBg = new(Color.FromArgb(0x28, 161, 161, 170));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PrerequisiteStatus status) return GrayBg;
        return status switch
        {
            PrerequisiteStatus.InstalledInSystem or PrerequisiteStatus.InstalledSuccess => GreenBg,
            PrerequisiteStatus.AvailableInGame => AmberBg,
            PrerequisiteStatus.NeedsDownload => BlueBg,
            PrerequisiteStatus.Installing => PurpleBg,
            PrerequisiteStatus.InstallFailed => RedBg,
            _ => GrayBg
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves a resource key string (e.g. "IconFlame") to its Application resource object.
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static readonly ResourceKeyConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return Application.Current.TryFindResource("IconExplore");

        return Application.Current.TryFindResource(key) ?? Application.Current.TryFindResource("IconExplore");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts TagType to foreground text brush.
/// </summary>
public sealed class TagTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(27, 144, 255));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(167, 139, 250));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush Rose = new(Color.FromRgb(244, 63, 94));
    private static readonly SolidColorBrush Sky = new(Color.FromRgb(14, 165, 233));
    private static readonly SolidColorBrush White = new(Color.FromRgb(244, 244, 245));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(161, 161, 170));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TagType tagType) return Muted;
        return tagType switch
        {
            TagType.Origin => Sky,
            TagType.UpdateAvailable => Amber,
            TagType.Status => Green,
            TagType.Engine => Purple,
            TagType.DlcCount => Purple,
            TagType.Platform => Accent,
            TagType.Drm => Amber,
            TagType.Nsfw => Rose,
            TagType.AppType => White,
            _ => Muted
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Converts TagType to background pill brush.
/// </summary>
public sealed class TagTypeToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AccentBg = new(Color.FromArgb(0x20, 27, 144, 255));
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(0x20, 16, 185, 129));
    private static readonly SolidColorBrush PurpleBg = new(Color.FromArgb(0x25, 167, 139, 250));
    private static readonly SolidColorBrush AmberBg = new(Color.FromArgb(0x20, 245, 158, 11));
    private static readonly SolidColorBrush RoseBg = new(Color.FromArgb(0x20, 244, 63, 94));
    private static readonly SolidColorBrush SkyBg = new(Color.FromArgb(0x20, 14, 165, 233));
    private static readonly SolidColorBrush InputBg = new(Color.FromRgb(24, 24, 27));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TagType tagType) return InputBg;
        return tagType switch
        {
            TagType.Origin => SkyBg,
            TagType.UpdateAvailable => AmberBg,
            TagType.Status => GreenBg,
            TagType.Engine => PurpleBg,
            TagType.DlcCount => PurpleBg,
            TagType.Platform => AccentBg,
            TagType.Drm => AmberBg,
            TagType.Nsfw => RoseBg,
            _ => InputBg
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Converts TagType to border brush.
/// </summary>
public sealed class TagTypeToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AccentBorder = new(Color.FromArgb(0x50, 27, 144, 255));
    private static readonly SolidColorBrush GreenBorder = new(Color.FromArgb(0x50, 16, 185, 129));
    private static readonly SolidColorBrush PurpleBorder = new(Color.FromArgb(0x60, 167, 139, 250));
    private static readonly SolidColorBrush AmberBorder = new(Color.FromArgb(0x50, 245, 158, 11));
    private static readonly SolidColorBrush RoseBorder = new(Color.FromArgb(0x50, 244, 63, 94));
    private static readonly SolidColorBrush SkyBorder = new(Color.FromArgb(0x50, 14, 165, 233));
    private static readonly SolidColorBrush Subtle = new(Color.FromArgb(0x30, 255, 255, 255));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TagType tagType) return Subtle;
        return tagType switch
        {
            TagType.Origin => SkyBorder,
            TagType.UpdateAvailable => AmberBorder,
            TagType.Status => GreenBorder,
            TagType.Engine => PurpleBorder,
            TagType.DlcCount => PurpleBorder,
            TagType.Platform => AccentBorder,
            TagType.Drm => AmberBorder,
            TagType.Nsfw => RoseBorder,
            _ => Subtle
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Converts BackgroundTaskStatus to color brush.
/// </summary>
public sealed class BackgroundTaskStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(161, 161, 170));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not BackgroundTaskStatus status) return Gray;
        return status switch
        {
            BackgroundTaskStatus.Running => Blue,
            BackgroundTaskStatus.Completed => Green,
            BackgroundTaskStatus.Failed => Red,
            BackgroundTaskStatus.Cancelled => Gray,
            _ => Blue
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Converts an image URL (or Steam AppID) to a high-performance cached, frozen ImageSource.
/// Reuses the exact same in-memory bitmap instance across all categories, search results, and views.
/// </summary>
public sealed class CachedImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var url = value.ToString();
        if (string.IsNullOrWhiteSpace(url)) return null;

        uint appId = 0;
        if (parameter is uint id) appId = id;
        else if (parameter is int intId && intId > 0) appId = (uint)intId;

        return BlueStar.App.Services.ImageCacheService.Instance.GetOrLoadImage(url, appId);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts LogSeverity to text foreground SolidColorBrush.
/// </summary>
public sealed class LogSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush VerboseBrush = new(Color.FromRgb(140, 145, 160));
    private static readonly SolidColorBrush DebugBrush = new(Color.FromRgb(150, 155, 175));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(251, 191, 36));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(248, 113, 113));
    private static readonly SolidColorBrush FatalBrush = new(Color.FromRgb(255, 82, 82));

    static LogSeverityToBrushConverter()
    {
        VerboseBrush.Freeze();
        DebugBrush.Freeze();
        InfoBrush.Freeze();
        WarningBrush.Freeze();
        ErrorBrush.Freeze();
        FatalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LogSeverity severity) return InfoBrush;
        return severity switch
        {
            LogSeverity.Verbose => VerboseBrush,
            LogSeverity.Debug => DebugBrush,
            LogSeverity.Information => InfoBrush,
            LogSeverity.Warning => WarningBrush,
            LogSeverity.Error => ErrorBrush,
            LogSeverity.Fatal => FatalBrush,
            _ => InfoBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts LogSeverity to badge background SolidColorBrush.
/// </summary>
public sealed class LogSeverityToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush VerboseBg = new(Color.FromArgb(35, 140, 145, 160));
    private static readonly SolidColorBrush DebugBg = new(Color.FromArgb(35, 150, 155, 175));
    private static readonly SolidColorBrush InfoBg = new(Color.FromArgb(40, 56, 189, 248));
    private static readonly SolidColorBrush WarningBg = new(Color.FromArgb(45, 251, 191, 36));
    private static readonly SolidColorBrush ErrorBg = new(Color.FromArgb(50, 248, 113, 113));
    private static readonly SolidColorBrush FatalBg = new(Color.FromArgb(60, 255, 82, 82));

    static LogSeverityToBgBrushConverter()
    {
        VerboseBg.Freeze();
        DebugBg.Freeze();
        InfoBg.Freeze();
        WarningBg.Freeze();
        ErrorBg.Freeze();
        FatalBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LogSeverity severity) return InfoBg;
        return severity switch
        {
            LogSeverity.Verbose => VerboseBg,
            LogSeverity.Debug => DebugBg,
            LogSeverity.Information => InfoBg,
            LogSeverity.Warning => WarningBg,
            LogSeverity.Error => ErrorBg,
            LogSeverity.Fatal => FatalBg,
            _ => InfoBg
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
