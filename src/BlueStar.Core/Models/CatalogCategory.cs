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
        set => SetField(ref _items, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public CatalogCategory()
    {
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
