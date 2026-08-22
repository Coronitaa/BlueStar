namespace BlueStar.Core.Models;

/// <summary>
/// Defines the various states a game instance can be in.
/// </summary>
public enum InstanceStatus
{
    /// <summary>
    /// The game instance is registered but not installed.
    /// </summary>
    NotInstalled = 0,

    /// <summary>
    /// The game instance is currently downloading files.
    /// </summary>
    Downloading = 1,

    /// <summary>
    /// The game instance is ready to play.
    /// </summary>
    Ready = 2,

    /// <summary>
    /// The game instance encountered an error.
    /// </summary>
    Error = 3,

    /// <summary>
    /// The game instance is currently being updated.
    /// </summary>
    Updating = 4,

    /// <summary>
    /// The game instance is currently running.
    /// </summary>
    Running = 5
}
