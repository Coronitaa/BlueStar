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
    private string? _drmName;
    private bool _hasAntiCheat;
    private string? _antiCheatName;
    private string? _antiCheatNotice;
    private bool _hasExternalLauncher;
    private string? _launcherName;
    private string? _launcherNotice;
    private bool _hasAccount;
    private string? _accountName;
    private string? _accountNotice;
    private bool _hasEula;
    private string? _eulaName;
    private string? _eulaNotice;
    private int? _priceCents;
    private long? _releaseDateUtc;
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
        set
        {
            if (SetField(ref _dlcCount, value))
            {
                OnPropertyChanged(nameof(DlcBadgeText));
            }
        }
    }

    /// <summary>
    /// Formatted badge text for DLC count (e.g. "27 DLCs", "1 DLC"), or null when 0.
    /// </summary>
    public string? DlcBadgeText => _dlcCount.HasValue && _dlcCount.Value > 0
        ? (_dlcCount.Value == 1 ? "1 DLC" : $"{_dlcCount.Value} DLCs")
        : null;

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
        get => _hasDrm || !string.IsNullOrWhiteSpace(_drmName);
        set
        {
            if (SetField(ref _hasDrm, value))
            {
                OnPropertyChanged(nameof(DrmBadgeText));
            }
        }
    }

    /// <summary>
    /// Gets or sets the 3rd-party DRM description notice if present.
    /// </summary>
    public string? DrmNotice
    {
        get => _drmNotice;
        set
        {
            if (SetField(ref _drmNotice, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_drmName))
                {
                    DrmName = Helpers.ThirdPartyNoticeParser.ExtractDrmName(value);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the specific DRM system name (e.g. "DENUVO", "VMProtect", "SecuROM").
    /// </summary>
    public string? DrmName
    {
        get => _drmName;
        set
        {
            if (SetField(ref _drmName, value))
            {
                OnPropertyChanged(nameof(DrmBadgeText));
                if (!string.IsNullOrWhiteSpace(value)) HasDrm = true;
            }
        }
    }

    /// <summary>
    /// Formatted badge text for DRM (e.g. "DENUVO", "VMProtect"), or "DRM" when system name is unknown.
    /// </summary>
    public string DrmBadgeText => !string.IsNullOrWhiteSpace(_drmName) ? _drmName.ToUpperInvariant() : "DRM";

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
        set
        {
            if (SetField(ref _reviewSummary, value))
            {
                OnPropertyChanged(nameof(ReviewDisplayText));
            }
        }
    }

    /// <summary>
    /// Percentage of positive reviews, when Steam reports one.
    /// </summary>
    public int? ReviewPercent
    {
        get => _reviewPercent;
        set
        {
            if (SetField(ref _reviewPercent, value))
            {
                OnPropertyChanged(nameof(ReviewDisplayText));
            }
        }
    }

    /// <summary>
    /// Formatted review string combining summary and real percentage, e.g. "Overwhelmingly Positive (100%)".
    /// </summary>
    public string? ReviewDisplayText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ReviewSummary))
            {
                return ReviewPercent.HasValue && ReviewPercent > 0 ? $"{ReviewPercent.Value}%" : null;
            }

            var summary = ReviewSummary.Trim();
            // If the summary is already just a percentage (e.g. "35%" or "35"), avoid "35% (35%)"
            if (summary.EndsWith("%") || (ReviewPercent.HasValue && summary == $"{ReviewPercent.Value}"))
            {
                if (ReviewPercent.HasValue && ReviewPercent.Value > 0)
                {
                    var derived = BlueStar.Core.Helpers.RatingEngine.GetReviewSummary(ReviewPercent.Value, 100);
                    return $"{derived} ({ReviewPercent.Value}%)";
                }
                return summary;
            }

            if (ReviewPercent.HasValue && ReviewPercent.Value > 0)
            {
                return $"{summary} ({ReviewPercent.Value}%)";
            }

            return summary;
        }
    }

    /// <summary>
    /// Current price as the store formats it, or a free-to-play marker.
    /// Regional pricing and discounts are preserved; does not fabricate static US dollar prices for paid games.
    /// </summary>
    public string? PriceText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_priceText)) return _priceText;
            if (_priceCents.HasValue && _priceCents.Value == 0)
            {
                return "Free";
            }
            if (_priceCents.HasValue && _priceCents.Value > 0) return $"${_priceCents.Value / 100.0:F2}";

            return null;
        }
        set => SetField(ref _priceText, value);
    }

    /// <summary>
    /// Gets or sets the price in integer cents (e.g. 5999 for $59.99, 0 for Free).
    /// </summary>
    public int? PriceCents
    {
        get => _priceCents;
        set
        {
            if (SetField(ref _priceCents, value))
            {
                OnPropertyChanged(nameof(PriceText));
            }
        }
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
    /// Falls back dynamically to <see cref="ReleaseDateUtc"/> if explicit text is absent.
    /// </summary>
    public string? ReleaseDateText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_releaseDateText)) return _releaseDateText;
            if (_releaseDateUtc.HasValue && _releaseDateUtc.Value > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(_releaseDateUtc.Value)
                    .ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(_version)) return _version;
            return null;
        }
        set => SetField(ref _releaseDateText, value);
    }

    /// <summary>
    /// Gets or sets the Unix epoch release timestamp in seconds.
    /// </summary>
    public long? ReleaseDateUtc
    {
        get => _releaseDateUtc;
        set
        {
            if (SetField(ref _releaseDateUtc, value))
            {
                OnPropertyChanged(nameof(ReleaseDateText));
            }
        }
    }

    /// <summary>
    /// Whether the app requires a third-party account or launcher (EA, Ubisoft Connect, PSN…).
    /// </summary>
    public bool HasExternalLauncher
    {
        get => _hasExternalLauncher || !string.IsNullOrWhiteSpace(_launcherName);
        set
        {
            if (SetField(ref _hasExternalLauncher, value))
            {
                OnPropertyChanged(nameof(LauncherBadgeText));
            }
        }
    }

    /// <summary>
    /// Gets or sets the 3rd-party launcher / account description notice if present.
    /// </summary>
    public string? LauncherNotice
    {
        get => _launcherNotice;
        set
        {
            if (SetField(ref _launcherNotice, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_launcherName))
                {
                    LauncherName = Helpers.ThirdPartyNoticeParser.ExtractLauncherName(value);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the specific external launcher system name (e.g. "Rockstar", "EA App", "Ubisoft").
    /// </summary>
    public string? LauncherName
    {
        get => _launcherName;
        set
        {
            if (SetField(ref _launcherName, value))
            {
                OnPropertyChanged(nameof(LauncherBadgeText));
                if (!string.IsNullOrWhiteSpace(value)) HasExternalLauncher = true;
            }
        }
    }

    /// <summary>
    /// Formatted badge text for external launcher (e.g. "Rockstar", "EA App"), or "Launcher" when system name is unknown.
    /// </summary>
    public string LauncherBadgeText => !string.IsNullOrWhiteSpace(_launcherName) ? _launcherName : "Launcher";

    /// <summary>
    /// Whether the app incorporates anti-cheat software (e.g. Easy Anti-Cheat, Denuvo, BattlEye, Vanguard).
    /// </summary>
    public bool HasAntiCheat
    {
        get => _hasAntiCheat || !string.IsNullOrWhiteSpace(_antiCheatName);
        set
        {
            if (SetField(ref _hasAntiCheat, value))
            {
                OnPropertyChanged(nameof(AntiCheatBadgeText));
            }
        }
    }

    /// <summary>
    /// Gets or sets the anti-cheat notice or details.
    /// </summary>
    public string? AntiCheatNotice
    {
        get => _antiCheatNotice;
        set
        {
            if (SetField(ref _antiCheatNotice, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_antiCheatName))
                {
                    AntiCheatName = Helpers.ThirdPartyNoticeParser.ExtractAntiCheatName(value);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the anti-cheat system name (e.g. "Easy Anti-Cheat", "BattlEye", "Denuvo", "VAC").
    /// </summary>
    public string? AntiCheatName
    {
        get => _antiCheatName;
        set
        {
            if (SetField(ref _antiCheatName, value))
            {
                OnPropertyChanged(nameof(AntiCheatBadgeText));
                if (!string.IsNullOrWhiteSpace(value)) HasAntiCheat = true;
            }
        }
    }

    /// <summary>
    /// Formatted badge text for anti-cheat software, or "Anti-Cheat" if name is generic.
    /// </summary>
    public string AntiCheatBadgeText => !string.IsNullOrWhiteSpace(_antiCheatName) ? _antiCheatName : "Anti-Cheat";

    /// <summary>
    /// Whether the app requires a 3rd-party account (e.g. 2K Sports, EA, Rockstar, PlayStation Network).
    /// </summary>
    public bool HasAccount
    {
        get => _hasAccount || _hasExternalLauncher || !string.IsNullOrWhiteSpace(_accountName) || !string.IsNullOrWhiteSpace(_launcherName);
        set
        {
            if (SetField(ref _hasAccount, value))
            {
                OnPropertyChanged(nameof(AccountBadgeText));
            }
        }
    }

    /// <summary>
    /// Gets or sets the 3rd-party account requirement notice.
    /// </summary>
    public string? AccountNotice
    {
        get => _accountNotice ?? _launcherNotice;
        set
        {
            if (SetField(ref _accountNotice, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_accountName))
                {
                    AccountName = Helpers.ThirdPartyNoticeParser.ExtractAccountName(value);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the 3rd-party account name (e.g. "2K Sports", "Rockstar", "EA Account").
    /// </summary>
    public string? AccountName
    {
        get => _accountName ?? _launcherName;
        set
        {
            if (SetField(ref _accountName, value))
            {
                OnPropertyChanged(nameof(AccountBadgeText));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    HasAccount = true;
                    if (string.IsNullOrWhiteSpace(_launcherName)) LauncherName = value;
                }
            }
        }
    }

    /// <summary>
    /// Formatted badge text for 3rd-party account.
    /// </summary>
    public string AccountBadgeText => !string.IsNullOrWhiteSpace(_accountName)
        ? _accountName
        : (!string.IsNullOrWhiteSpace(_launcherName) ? _launcherName : "Account");

    /// <summary>
    /// Whether the app requires accepting a 3rd-party EULA / ALUF.
    /// </summary>
    public bool HasEula
    {
        get => _hasEula || !string.IsNullOrWhiteSpace(_eulaName);
        set
        {
            if (SetField(ref _hasEula, value))
            {
                OnPropertyChanged(nameof(EulaBadgeText));
            }
        }
    }

    /// <summary>
    /// Gets or sets the EULA/ALUF notice or URL.
    /// </summary>
    public string? EulaNotice
    {
        get => _eulaNotice;
        set
        {
            if (SetField(ref _eulaNotice, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_eulaName))
                {
                    EulaName = Helpers.ThirdPartyNoticeParser.ExtractEulaName(value);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the EULA/ALUF display name (e.g. "ARC Raiders EULA", "NBA 2K25 EULA", or "ALUF").
    /// </summary>
    public string? EulaName
    {
        get => _eulaName;
        set
        {
            if (SetField(ref _eulaName, value))
            {
                OnPropertyChanged(nameof(EulaBadgeText));
                if (!string.IsNullOrWhiteSpace(value)) HasEula = true;
            }
        }
    }

    /// <summary>
    /// Formatted badge text for EULA/ALUF.
    /// </summary>
    public string EulaBadgeText => !string.IsNullOrWhiteSpace(_eulaName) ? _eulaName : "ALUF";

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
