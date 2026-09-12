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

    private IReadOnlyList<string> _tags = [];

    /// <summary>
    /// Gets or sets any specific tags or emulator features (e.g. "BYPASS", "ONLINE", "REFIX").
    /// </summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set => SetField(ref _tags, value);
    }

    private IReadOnlyList<int> _tagIds = [];
    private IReadOnlyList<StoreTagRef> _storeTags = [];
    private string? _reviewSummary;
    private int? _reviewPercent;
    private string? _priceText;
    private string? _originalPriceText;
    private int _discountPercent;
    private string? _deckCompatibility;
    private string? _releaseDateText;
    private bool _hasExternalLauncher;
    private bool _isEnriched;
    private RequirementsVerdict _requirements = RequirementsVerdict.Unknown;
    private int _matchedTagCount;

    /// <summary>
    /// Steam store tag ids for this app, straight from <c>data-ds-tagids</c> on the search row.
    /// </summary>
    public IReadOnlyList<int> TagIds
    {
        get => _tagIds;
        set => SetField(ref _tagIds, value);
    }

    /// <summary>
    /// Store tags, resolved from <see cref="TagIds"/> against the catalog. These are the bubbles
    /// shown on the card; each knows whether it is one of the tags being filtered by.
    /// </summary>
    public IReadOnlyList<StoreTagRef> StoreTags
    {
        get => _storeTags;
        set => SetField(ref _storeTags, value);
    }

    /// <summary>
    /// Steam review summary, e.g. "Very Positive".
    /// </summary>
    public string? ReviewSummary
    {
        get => _reviewSummary;
        set => SetField(ref _reviewSummary, value);
    }

    /// <summary>
    /// Percentage of positive reviews, when Steam reports one.
    /// </summary>
    public int? ReviewPercent
    {
        get => _reviewPercent;
        set => SetField(ref _reviewPercent, value);
    }

    /// <summary>
    /// Current price as the store formats it, or a free-to-play marker.
    /// </summary>
    public string? PriceText
    {
        get => _priceText;
        set => SetField(ref _priceText, value);
    }

    /// <summary>
    /// Pre-discount price, only set while <see cref="DiscountPercent"/> is non-zero.
    /// </summary>
    public string? OriginalPriceText
    {
        get => _originalPriceText;
        set => SetField(ref _originalPriceText, value);
    }

    /// <summary>
    /// Active discount, 0 when the item is not on sale.
    /// </summary>
    public int DiscountPercent
    {
        get => _discountPercent;
        set => SetField(ref _discountPercent, value);
    }

    /// <summary>
    /// Steam Deck rating: "Verified", "Playable", or <c>null</c> when unrated.
    /// </summary>
    public string? DeckCompatibility
    {
        get => _deckCompatibility;
        set => SetField(ref _deckCompatibility, value);
    }

    /// <summary>
    /// Release date as the store prints it.
    /// </summary>
    public string? ReleaseDateText
    {
        get => _releaseDateText;
        set => SetField(ref _releaseDateText, value);
    }

    /// <summary>
    /// Whether the app requires a third-party account or launcher (EA, Ubisoft Connect, PSN…).
    /// Only meaningful once <see cref="IsEnriched"/> is true.
    /// </summary>
    public bool HasExternalLauncher
    {
        get => _hasExternalLauncher;
        set => SetField(ref _hasExternalLauncher, value);
    }

    /// <summary>
    /// Whether the <c>appdetails</c> pass has run for this result. Until it has, DRM, external
    /// launcher and DLC count are unknown and the card shows a pending badge.
    /// </summary>
    public bool IsEnriched
    {
        get => _isEnriched;
        set => SetField(ref _isEnriched, value);
    }

    /// <summary>
    /// How the detected hardware compares to this app's stated requirements.
    /// </summary>
    public RequirementsVerdict Requirements
    {
        get => _requirements;
        set => SetField(ref _requirements, value);
    }

    /// <summary>
    /// How many of the tags the person selected this result actually carries.
    /// </summary>
    /// <remarks>
    /// Drives the ordering when the search has relaxed an AND into partial matches: the closest
    /// results stay at the top instead of being mixed in with single-tag ones.
    /// </remarks>
    public int MatchedTagCount
    {
        get => _matchedTagCount;
        set => SetField(ref _matchedTagCount, value);
    }

    /// <summary>
    /// Gets the direct URL to SteamDB for this app.
    /// </summary>
    public string SteamDbUrl => $"https://steamdb.info/app/{AppId}/";

    /// <summary>
    /// Gets the store page URL for this app.
    /// </summary>
    public string StorePageUrl => $"https://store.steampowered.com/app/{AppId}/";

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
