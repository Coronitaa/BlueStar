using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a catalog category feed (e.g. Trending, Most Played, SteamDB lists, DepotBox Webhooks)
/// displayed as an auto-scrolling carousel or expanded vertical grid.
/// </summary>
public class CatalogCategory : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _iconKey = "IconExplore";
    private string _badgeText = string.Empty;
    private string _tagColor = "#3B82F6";
    private ObservableCollection<SearchResult> _items = [];
    private bool _isLoading = true;
    private bool _isExpanded;
    private string? _errorMessage;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetField(ref _subtitle, value);
    }

    public string IconKey
    {
        get => _iconKey;
        set => SetField(ref _iconKey, value);
    }

    public string BadgeText
    {
        get => _badgeText;
        set => SetField(ref _badgeText, value);
    }

    public string TagColor
    {
        get => _tagColor;
        set => SetField(ref _tagColor, value);
    }

    public ObservableCollection<SearchResult> Items
    {
        get => _items;
        set
        {
            var val = value ?? [];
            if (_items != null)
            {
                _items.CollectionChanged -= OnItemsCollectionChanged;
            }
            _items = val;
            _items.CollectionChanged += OnItemsCollectionChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVisibleCategory));
        }
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsVisibleCategory));
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsVisibleCategory));
            }
        }
    }

    /// <summary>
    /// Category is visible if it's currently loading, or if it has 1 or more games.
    /// If empty after loading, it will be automatically hidden.
    /// </summary>
    public bool IsVisibleCategory => IsLoading || (_items != null && _items.Count > 0);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    private bool _hasMoreItems = true;
    private bool _isLoadingMore;
    private int _displayLimit = 9;

    /// <summary>
    /// Full candidate pool of games for this category (pre-pagination and replenishment buffer).
    /// </summary>
    public List<SearchResult> PoolItems { get; set; } = [];

    /// <summary>
    /// Current number of items to display in this category. Default is 9 slots.
    /// </summary>
    public int DisplayLimit
    {
        get => _displayLimit;
        set => SetField(ref _displayLimit, value);
    }

    /// <summary>
    /// Whether more items can be loaded for this category.
    /// </summary>
    public bool HasMoreItems
    {
        get => _hasMoreItems;
        set => SetField(ref _hasMoreItems, value);
    }

    /// <summary>
    /// Whether additional items are actively being fetched for this category.
    /// </summary>
    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        set => SetField(ref _isLoadingMore, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public CatalogCategory()
    {
        if (_items != null)
        {
            _items.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    public CatalogCategory(string id, string title, string subtitle, string iconKey, string tagColor, string badgeText = "")
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        IconKey = iconKey;
        TagColor = tagColor;
        BadgeText = badgeText;
        IsLoading = true;
        IsExpanded = false;
        if (_items != null)
        {
            _items.CollectionChanged += OnItemsCollectionChanged;
        }
    }

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
