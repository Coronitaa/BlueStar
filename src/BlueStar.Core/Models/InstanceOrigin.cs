namespace BlueStar.Core.Models;

/// <summary>
/// Defines the creation origin and management model of a game instance.
/// The provider is a resolution strategy, never part of this persisted identity.
/// </summary>
public enum InstanceOrigin
{
    /// <summary>
    /// Managed instance with full depot, build, and manifest lifecycle management.
    /// Identifier preserved as DepotBox (value 0) for backward compatibility with pre-1.3 serialized instance JSON.
    /// </summary>
    DepotBox = 0,

    /// <summary>
    /// Imported from a local Steam library. Managed via Steam metadata.
    /// </summary>
    Steam = 1,

    /// <summary>
    /// Imported from an arbitrary folder on disk.
    /// </summary>
    ImportedFolder = 2
}
