namespace BlueStar.Core.Models;

/// <summary>
/// Defines the creation origin and management model of a game instance.
/// </summary>
public enum InstanceOrigin
{
    /// <summary>
    /// Downloaded or created via DepotBox with full depot and manifest management.
    /// </summary>
    DepotBox = 0,

    /// <summary>
    /// Imported from a local Steam library. Managed via Steam metadata; Files & Depots and DepotBox update checks are hidden.
    /// </summary>
    Steam = 1,

    /// <summary>
    /// Imported from an arbitrary folder. Can be associated with DepotBox to enable updates and depot tracking.
    /// </summary>
    ImportedFolder = 2
}
