using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlueStar.Core.Models;

/// <summary>
/// One Steam store tag as it appears on a result card.
/// </summary>
/// <remarks>
/// A bare string would do for display, but the card also has to show which of its tags the
/// person is currently filtering by, and that answer changes while the card is on screen. Making
/// the tag a small object that raises its own change notification lets the bubble bind straight
/// to <see cref="IsActive"/>, instead of reaching back up the visual tree for the filter state
/// through a converter.
/// </remarks>
public sealed class StoreTagRef : INotifyPropertyChanged
{
    private bool _isActive;

    /// <summary>Display name, in the store language.</summary>
    public string Name { get; }

    /// <summary>Whether this tag is part of the current query.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreTagRef"/> class.
    /// </summary>
    public StoreTagRef(string name, bool isActive = false)
    {
        Name = name;
        _isActive = isActive;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
