using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents an individual manifest item belonging to a specific depot of a game.
/// </summary>
public sealed record GameManifestItemModel
{
    public uint DepotId { get; init; }
    public ulong ManifestId { get; init; }
    public string DepotName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string SizeFormatted => FormatBytes(SizeBytes);
    
    /// <summary>
    /// Clean user-facing provenance label (e.g. "Steam DepotCache", "Local Cache", "Manifest Hub").
    /// </summary>
    public string CleanProvenance { get; init; } = "Local";
    public bool IsReadyToContribute { get; init; } = true;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "Desconocido";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double s = bytes;
        while (s >= 1024 && i < suffixes.Length - 1) { s /= 1024; i++; }
        return $"{s:0.##} {suffixes[i]}";
    }
}

/// <summary>
/// Top-level group grouping all depot manifests associated with a specific game.
/// </summary>
public sealed class GameManifestGroup : INotifyPropertyChanged
{
    public uint AppId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public string HeaderImageUrl => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";
    
    public IReadOnlyList<GameManifestItemModel> Manifests { get; init; } = [];
    public int ManifestCount => Manifests.Count;
    public long TotalSizeBytes => Manifests.Sum(m => m.SizeBytes);
    public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double s = bytes;
        while (s >= 1024 && i < suffixes.Length - 1) { s /= 1024; i++; }
        return $"{s:0.##} {suffixes[i]}";
    }
}
