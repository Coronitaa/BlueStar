using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// One selectable bubble in a filter group.
/// </summary>
public partial class FilterOptionItem : ObservableObject
{
    /// <summary>The Steam facet this bubble maps to.</summary>
    public SteamFacetOption Option { get; }

    /// <summary>Stable key, used for usage counting and pinning.</summary>
    public string Key => Option.Kind + "|" + Option.Value;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private int? _productCount;

    [ObservableProperty]
    private FacetState _state = FacetState.Neutral;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isHighlighted;

    [ObservableProperty]
    private System.Windows.Media.Brush? _hoverBackgroundBrush;

    [ObservableProperty]
    private System.Windows.Media.Brush? _hoverBorderBrush;

    [ObservableProperty]
    private System.Windows.Media.Brush? _hoverForegroundBrush;

    /// <summary>Whether the bubble is currently part of the query.</summary>
    public bool IsActive => State != FacetState.Neutral;

    /// <summary>
    /// Product count abbreviated for display: 98 085 becomes "98,1k".
    /// </summary>
    public string CountText => ProductCount is null ? string.Empty : Abbreviate(ProductCount.Value);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterOptionItem"/> class.
    /// </summary>
    public FilterOptionItem(SteamFacetOption option, string displayName, int? productCount = null)
    {
        Option = option ?? throw new ArgumentNullException(nameof(option));
        _displayName = displayName;
        _productCount = productCount;
    }

    partial void OnStateChanged(FacetState value) => OnPropertyChanged(nameof(IsActive));

    partial void OnProductCountChanged(int? value) => OnPropertyChanged(nameof(CountText));

    /// <summary>
    /// Abbreviates a count the way the filter panel shows it.
    /// </summary>
    public static string Abbreviate(int value)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        // int tops out just above two billion, so "B" never needs a no-decimal form.
        return value switch
        {
            >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#", culture) + "B",
            >= 1_000_000 => (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.#", culture) + "M",
            >= 1_000 => (value / 1_000d).ToString(value >= 10_000 ? "0" : "0.#", culture) + "k",
            _ => value.ToString(culture)
        };
    }
}

/// <summary>
/// A collapsible group of filter bubbles.
/// </summary>
/// <remarks>
/// A group shows a limited number of bubbles. When it holds more, a search box appears; a bubble
/// found through search and then selected is pinned into the visible list and displaces the
/// least-used one, so the panel keeps its height while adapting to how the person actually
/// filters.
/// </remarks>
public partial class FilterGroupViewModel : ObservableObject
{
    private readonly List<FilterOptionItem> _all = [];
    private readonly IDictionary<string, int> _usage;
    private readonly Action _onChanged;
    private readonly Action<FilterGroupViewModel>? _onNeedsCounts;
    private int _categorySelectionCounter;
    private readonly Dictionary<string, int> _lastSelectedAt = new(StringComparer.Ordinal);

    /// <summary>Stable group identifier.</summary>
    public string Key { get; }

    /// <summary>Key of the geometry in <c>Icons.xaml</c> shown in the header.</summary>
    public string IconKey { get; }

    /// <summary>Whether only one option in this group may be active at a time.</summary>
    public bool IsSingleChoice { get; }

    /// <summary>Whether the options here can be excluded as well as included.</summary>
    public bool SupportsExclude { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isExpandedAll;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<FilterOptionItem> _items = [];

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _hiddenCount;

    /// <summary>Number of selections made within this category, used for highlight decay.</summary>
    public int CategorySelectionCounter
    {
        get => _categorySelectionCounter;
        internal set => _categorySelectionCounter = value;
    }

    /// <summary>Index tracking when each option key was last selected.</summary>
    public IReadOnlyDictionary<string, int> LastSelectedAt => _lastSelectedAt;

    /// <summary>
    /// Whether this group can actually filter yet. A group that is not available shows its
    /// bubbles switched off rather than accepting clicks that would do nothing.
    /// </summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>
    /// Whether the group is shown at all. The adult-content group disappears entirely while
    /// adult content is switched off in settings, rather than sitting there inert.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>How many bubbles are shown before the search box takes over.</summary>
    public int VisibleCount { get; }

    /// <summary>Whether this group holds more options than it can show at once.</summary>
    public bool IsSearchable => _all.Count > VisibleCount || _all.Count > 20;

    /// <summary>Whether this group holds more than 20 tags to display the random pick dice.</summary>
    public bool HasRandomPick => _all.Count > 20;

    /// <summary>Every option in the group, selected or not.</summary>
    public IReadOnlyList<FilterOptionItem> AllOptions => _all;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterGroupViewModel"/> class.
    /// </summary>
    /// <param name="key">Stable group identifier.</param>
    /// <param name="title">Localized header text.</param>
    /// <param name="iconKey">Geometry key for the header icon.</param>
    /// <param name="visibleCount">How many bubbles to show before collapsing the rest.</param>
    /// <param name="isExpanded">Whether the group starts open.</param>
    /// <param name="usage">Shared usage counter, used to decide which bubbles stay visible.</param>
    /// <param name="onChanged">Invoked whenever a selection changes.</param>
    /// <param name="onNeedsCounts">Invoked when newly visible bubbles need their counts resolved.</param>
    /// <param name="isSingleChoice">Whether only one option may be active at a time.</param>
    /// <param name="supportsExclude">Whether options can be excluded as well as included.</param>
    public FilterGroupViewModel(
        string key,
        string title,
        string iconKey,
        int visibleCount,
        bool isExpanded,
        IDictionary<string, int> usage,
        Action onChanged,
        Action<FilterGroupViewModel>? onNeedsCounts = null,
        bool isSingleChoice = false,
        bool supportsExclude = true)
    {
        Key = key;
        _title = title;
        IconKey = iconKey;
        VisibleCount = Math.Max(4, visibleCount);
        _isExpanded = isExpanded;
        _usage = usage ?? new Dictionary<string, int>();
        _onChanged = onChanged ?? (() => { });
        _onNeedsCounts = onNeedsCounts;
        IsSingleChoice = isSingleChoice;
        SupportsExclude = supportsExclude && !isSingleChoice;
    }

    /// <summary>
    /// Toggles between showing default limited tags and all available tags for this group.
    /// </summary>
    [RelayCommand]
    public void ToggleExpandAll()
    {
        IsExpandedAll = !IsExpandedAll;
        Refresh();
    }

    /// <summary>
    /// Replaces the group's options and rebuilds the visible list.
    /// </summary>
    public void SetOptions(IEnumerable<FilterOptionItem> options)
    {
        _all.Clear();
        int index = 0;
        foreach (var opt in options)
        {
            ApplyCategoryPastelBrushes(opt, index++);
            _all.Add(opt);
        }
        UpdateHighlights();
        OnPropertyChanged(nameof(IsSearchable));
        OnPropertyChanged(nameof(HasRandomPick));
        Refresh();
    }

    /// <summary>
    /// Cycles an option: neutral to included, included to excluded, excluded back to neutral.
    /// Single-choice groups only toggle between neutral and included.
    /// </summary>
    [RelayCommand]
    public void Toggle(FilterOptionItem? item)
    {
        if (item is null) return;

        if (IsSingleChoice)
        {
            var wasActive = item.State != FacetState.Neutral;
            foreach (var other in _all) other.State = FacetState.Neutral;
            item.State = wasActive ? FacetState.Neutral : FacetState.Include;
        }
        else
        {
            item.State = item.State switch
            {
                FacetState.Neutral => FacetState.Include,
                FacetState.Include when SupportsExclude => FacetState.Exclude,
                _ => FacetState.Neutral
            };
        }

        AfterSelection(item);
    }

    /// <summary>
    /// Excludes an option directly, or clears it if it was already excluded.
    /// </summary>
    [RelayCommand]
    public void Exclude(FilterOptionItem? item)
    {
        if (item is null || IsSingleChoice || !SupportsExclude) return;

        item.State = item.State == FacetState.Exclude ? FacetState.Neutral : FacetState.Exclude;
        AfterSelection(item);
    }

    /// <summary>
    /// Selects one option at random from those not already active.
    /// </summary>
    [RelayCommand]
    public void PickRandom()
    {
        var pool = _all.Where(o => o.State == FacetState.Neutral).ToList();
        if (pool.Count == 0) return;

        var pick = pool[Random.Shared.Next(pool.Count)];

        if (IsSingleChoice)
        {
            foreach (var other in _all) other.State = FacetState.Neutral;
        }

        pick.State = FacetState.Include;
        AfterSelection(pick);
    }

    /// <summary>
    /// Clears every selection in this group.
    /// </summary>
    public void ClearSelection()
    {
        foreach (var option in _all) option.State = FacetState.Neutral;
        Refresh();
    }

    /// <summary>
    /// Normalizes a string by stripping non-alphanumeric characters for fuzzy matching.
    /// </summary>
    public static string CleanKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Adds or activates an ad-hoc custom option into this filter group.
    /// </summary>
    public void AddCustomOption(FilterOptionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var existing = _all.FirstOrDefault(o => o.Key == item.Key || o.Option.Value == item.Option.Value);
        if (existing != null)
        {
            existing.State = FacetState.Include;
            existing.IsPinned = true;
            ApplyCategoryPastelBrushes(existing);
        }
        else
        {
            item.State = FacetState.Include;
            item.IsPinned = true;
            ApplyCategoryPastelBrushes(item);
            _all.Add(item);
        }
        _usage[item.Key] = _usage.TryGetValue(item.Key, out var used) ? used + 1 : 1;
        _categorySelectionCounter++;
        _lastSelectedAt[item.Key] = _categorySelectionCounter;
        UpdateHighlights();
        Refresh();
        _onChanged();
    }

    /// <summary>
    /// Activates an option by its display name, pinning it so it stays visible. Used when a tag
    /// is clicked on a result card or picked from the "similar games" action.
    /// </summary>
    /// <returns><c>true</c> if the group owns an option with that name.</returns>
    public bool ActivateByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var cleanTarget = CleanKey(name);
        var match = _all.FirstOrDefault(o =>
            string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(o.Option.FallbackName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(CleanKey(o.DisplayName), cleanTarget, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(CleanKey(o.Option.FallbackName), cleanTarget, StringComparison.OrdinalIgnoreCase));

        // Substring / keyword fuzzy match for compound feature names (e.g. "DualShock Controller Support" vs "DUALSHOCK support" in controller/features)
        if (match is null && cleanTarget.Length >= 4)
        {
            if (Key is "controller" or "features" or "accessibility")
            {
                match = _all.FirstOrDefault(o =>
                {
                    var cleanDisplay = CleanKey(o.DisplayName);
                    var cleanFallback = CleanKey(o.Option.FallbackName);
                    return (cleanDisplay.Length >= 4 && (cleanTarget.Contains(cleanDisplay) || cleanDisplay.Contains(cleanTarget))) ||
                           (cleanFallback.Length >= 4 && (cleanTarget.Contains(cleanFallback) || cleanFallback.Contains(cleanTarget)));
                });
            }
        }

        if (match is null) return false;

        match.State = FacetState.Include;
        match.IsPinned = true;
        _usage[match.Key] = _usage.TryGetValue(match.Key, out var used) ? used + 1 : 1;
        _categorySelectionCounter++;
        _lastSelectedAt[match.Key] = _categorySelectionCounter;
        UpdateHighlights();
        Refresh();
        return true;
    }

    /// <summary>Selected facets in this group, ready to go into a query.</summary>
    public IEnumerable<KeyValuePair<SteamFacetOption, FacetState>> ActiveFacets =>
        _all.Where(o => o.State != FacetState.Neutral)
            .Select(o => new KeyValuePair<SteamFacetOption, FacetState>(o.Option, o.State));

    /// <summary>Every active option, for the chip bar above the results.</summary>
    public IEnumerable<FilterOptionItem> ActiveOptions => _all.Where(o => o.State != FacetState.Neutral);

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnIsExpandedChanged(bool value)
    {
        // Counts are only worth fetching for bubbles someone can actually see.
        if (value) Refresh();
    }

    private void AfterSelection(FilterOptionItem item)
    {
        if (item.State != FacetState.Neutral)
        {
            _usage[item.Key] = _usage.TryGetValue(item.Key, out var used) ? used + 1 : 1;
            _categorySelectionCounter++;
            _lastSelectedAt[item.Key] = _categorySelectionCounter;

            // A bubble reached through search earns its place in the visible list.
            if (!string.IsNullOrWhiteSpace(SearchText)) item.IsPinned = true;
        }

        UpdateHighlights();
        SearchText = string.Empty;
        Refresh();
        _onChanged();
    }

    /// <summary>
    /// Updates the selected-count badge without rebuilding the bubble list.
    /// </summary>
    public void RefreshBadgeOnly()
    {
        SelectedCount = _all.Count(o => o.State != FacetState.Neutral);
    }

    private static readonly (Brush bg, Brush border, Brush text)[] PastelPalettes;

    static FilterGroupViewModel()
    {
        PastelPalettes = new (Brush, Brush, Brush)[]
        {
            CreateFrozenPalette("#221C2E", "#63527D", "#DDD6FE"), // Lavender / Violet
            CreateFrozenPalette("#18241F", "#466E5E", "#A7F3D0"), // Mint / Emerald
            CreateFrozenPalette("#261E17", "#7A573E", "#FED7AA"), // Peach / Amber
            CreateFrozenPalette("#27181F", "#7D4258", "#FECDD3"), // Rose / Coral
            CreateFrozenPalette("#17222E", "#476887", "#BAE6FD"), // Sky / Cerulean
            CreateFrozenPalette("#1B1E28", "#4F5A7B", "#C7D2FE"), // Periwinkle / Indigo
            CreateFrozenPalette("#162424", "#3D6F6F", "#99F6E4"), // Seafoam / Teal
            CreateFrozenPalette("#262217", "#7A693E", "#FEF08A"), // Warm Butter / Gold
            CreateFrozenPalette("#271724", "#7D3E6F", "#FBCFE8"), // Orchid / Fuchsia
            CreateFrozenPalette("#1E2519", "#566E44", "#D9F99D"), // Sage / Lime
            CreateFrozenPalette("#251B27", "#6D4975", "#E9D5FF"), // Soft Mauve / Lilac
            CreateFrozenPalette("#281B1B", "#7B4B4B", "#FED7D7")  // Soft Blush / Melon
        };
    }

    private static (Brush bg, Brush border, Brush text) CreateFrozenPalette(string bgHex, string borderHex, string textHex)
    {
        var bg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgHex));
        var border = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderHex));
        var text = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textHex));
        bg.Freeze();
        border.Freeze();
        text.Freeze();
        return (bg, border, text);
    }

    /// <summary>
    /// Restores persisted category selection count and last selected indices for highlight evaluation.
    /// </summary>
    public void RestoreCategoryUsage(int counter, IDictionary<string, int>? lastSelectedAt)
    {
        _categorySelectionCounter = counter;
        _lastSelectedAt.Clear();
        if (lastSelectedAt != null)
        {
            foreach (var (k, v) in lastSelectedAt)
            {
                _lastSelectedAt[k] = v;
            }
        }
        UpdateHighlights();
    }

    /// <summary>
    /// Evaluates highlight state for all options in this group:
    /// Selected at least 5 times, decaying after 50 selections of other tags in this category.
    /// </summary>
    public void UpdateHighlights()
    {
        foreach (var option in _all)
        {
            var usage = _usage.TryGetValue(option.Key, out var count) ? count : 0;
            var lastIndex = _lastSelectedAt.TryGetValue(option.Key, out var idx) ? idx : 0;
            option.IsHighlighted = usage >= 5 && (_categorySelectionCounter - lastIndex) <= 50;
        }
    }

    /// <summary>
    /// Applies varied, soft pastel hover brushes to each individual tag within every category.
    /// Distributes across 12 low-glare pastel palettes to ensure visual diversity.
    /// </summary>
    public void ApplyCategoryPastelBrushes(FilterOptionItem item, int index = -1)
    {
        var paletteIndex = index >= 0
            ? index % PastelPalettes.Length
            : (Math.Abs(item.DisplayName?.GetHashCode() ?? 0) % PastelPalettes.Length);

        var (bg, border, text) = PastelPalettes[paletteIndex];
        item.HoverBackgroundBrush = bg;
        item.HoverBorderBrush = border;
        item.HoverForegroundBrush = text;
    }

    /// <summary>
    /// Rebuilds the visible bubbles: whatever is active or pinned, then the most-used of the
    /// group's leading options to fill the remaining slots.
    /// </summary>
    public void Refresh()
    {
        SelectedCount = _all.Count(o => o.State != FacetState.Neutral);

        List<FilterOptionItem> visible;
        var query = SearchText?.Trim();

        if (!string.IsNullOrEmpty(query))
        {
            var matches = _all
                .Where(o => o.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                            (!string.IsNullOrEmpty(o.Option.FallbackName) && o.Option.FallbackName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();

            if (IsExpandedAll || matches.Count <= VisibleCount + 1)
            {
                visible = matches;
                HiddenCount = 0;
            }
            else
            {
                visible = matches.Take(VisibleCount).ToList();
                var hidden = matches.Count - visible.Count;
                if (hidden <= 1)
                {
                    visible = matches;
                    HiddenCount = 0;
                }
                else
                {
                    HiddenCount = hidden;
                }
            }
        }
        else if (IsExpandedAll || _all.Count <= VisibleCount + 1)
        {
            visible = [.. _all];
            HiddenCount = 0;
        }
        else
        {
            var forced = _all.Where(o => o.State != FacetState.Neutral || o.IsPinned).ToList();
            var room = Math.Max(VisibleCount - forced.Count, 4);

            var filler = _all
                .Take(VisibleCount)
                .Where(o => !forced.Contains(o))
                .OrderByDescending(o => _usage.TryGetValue(o.Key, out var used) ? used : 0)
                .Take(room)
                .ToList();

            var chosen = forced.Concat(filler).ToHashSet();
            visible = _all.Where(chosen.Contains).ToList();
            var hidden = _all.Count - visible.Count;
            if (hidden <= 1)
            {
                visible = [.. _all];
                HiddenCount = 0;
            }
            else
            {
                HiddenCount = hidden;
            }
        }

        if (Items.Count == visible.Count && Items.SequenceEqual(visible))
        {
            _onNeedsCounts?.Invoke(this);
            return;
        }

        Items = new ObservableCollection<FilterOptionItem>(visible);
        _onNeedsCounts?.Invoke(this);
    }
}

/// <summary>
/// One entry in the sort selector.
/// </summary>
public sealed partial class SortOptionItem : ObservableObject
{
    public string Value { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled = true;

    public SortOptionItem(string value, string displayName, bool isEnabled = true)
    {
        Value = value;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }
}
