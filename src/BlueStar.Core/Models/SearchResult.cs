using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the result of a game search query with rich DepotBox &amp; SteamDB metadata.
/// </summary>
public class SearchResult : INotifyPropertyChanged
{
    private uint _appId;
    private string _name = string.Empty;
    private bool _isAvailable = true;
    private int? _dlcCount;
    private string? _headerImageUrl;
    private string _appType = "Game";
    private string? _version;
    private bool _hasWindows = true;
    private bool _hasLinux;
    private bool _hasMac;
    private bool _isDlc;
    private bool _isRedistributable;
    private bool _isNsfw;
    private bool _hasDrm;
    private string? _drmNotice;
    private bool _isCreating;
    private string? _creationStatus;

    /// <summary>
    /// Gets or sets the application identifier.
    /// </summary>
    public uint AppId
    {
        get => _appId;
        set => SetField(ref _appId, value);
    }

    /// <summary>
    /// Gets or sets the name of the game or software.
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the package is available for download.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetField(ref _isAvailable, value);
    }

    /// <summary>
    /// Gets or sets the number of DLCs available for the game, if known.
    /// </summary>
    public int? DlcCount
    {
        get => _dlcCount;
        set => SetField(ref _dlcCount, value);
    }

    /// <summary>
    /// Gets or sets the URL to the header image / banner for the game.
    /// </summary>
    public string? HeaderImageUrl
    {
        get => _headerImageUrl;
        set => SetField(ref _headerImageUrl, value);
    }

    /// <summary>
    /// Gets or sets the type of application ("Game", "Application", "Tool").
    /// </summary>
    public string AppType
    {
        get => _appType;
        set => SetField(ref _appType, value);
    }

    /// <summary>
    /// Gets or sets the version or build information if available.
    /// </summary>
    public string? Version
    {
        get => _version;
        set => SetField(ref _version, value);
    }

    /// <summary>
    /// Gets or sets whether Windows OS is supported.
    /// </summary>
    public bool HasWindows
    {
        get => _hasWindows;
        set => SetField(ref _hasWindows, value);
    }

    /// <summary>
    /// Gets or sets whether Linux/SteamOS is supported.
    /// </summary>
    public bool HasLinux
    {
        get => _hasLinux;
        set => SetField(ref _hasLinux, value);
    }

    /// <summary>
    /// Gets or sets whether macOS is supported.
    /// </summary>
    public bool HasMac
    {
        get => _hasMac;
        set => SetField(ref _hasMac, value);
    }

    /// <summary>
    /// Gets or sets whether this item is a DLC.
    /// </summary>
    public bool IsDlc
    {
        get => _isDlc;
        set => SetField(ref _isDlc, value);
    }

    /// <summary>
    /// Gets or sets whether this item is a Redistributable package.
    /// </summary>
    public bool IsRedistributable
    {
        get => _isRedistributable;
        set => SetField(ref _isRedistributable, value);
    }

    /// <summary>
    /// Gets or sets whether this item is marked as NSFW / Adult content.
    /// </summary>
    public bool IsNsfw
    {
        get => _isNsfw;
        set => SetField(ref _isNsfw, value);
    }

    /// <summary>
    /// Gets or sets whether this item incorporates 3rd-party DRM (Denuvo, EA Account, Ubisoft Connect, etc.).
    /// </summary>
    public bool HasDrm
    {
        get => _hasDrm;
        set => SetField(ref _hasDrm, value);
    }

    /// <summary>
    /// Gets or sets the 3rd-party DRM description notice if present.
    /// </summary>
    public string? DrmNotice
    {
        get => _drmNotice;
        set => SetField(ref _drmNotice, value);
    }

    /// <summary>
    /// Gets or sets whether an instance is actively being created for this item.
    /// </summary>
    public bool IsCreating
    {
        get => _isCreating;
        set => SetField(ref _isCreating, value);
    }

    /// <summary>
    /// Gets or sets the active status/progress message when creating instance.
    /// </summary>
    public string? CreationStatus
    {
        get => _creationStatus;
        set => SetField(ref _creationStatus, value);
    }

    /// <summary>
    /// Gets the direct URL to SteamDB for this app.
    /// </summary>
    public string SteamDbUrl => $"https://steamdb.info/app/{AppId}/";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
