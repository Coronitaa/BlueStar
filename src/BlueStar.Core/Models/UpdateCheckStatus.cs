namespace BlueStar.Core.Models;

/// <summary>
/// Represents the state of an instance update check.
/// </summary>
public enum UpdateCheckStatus
{
    /// <summary>
    /// The update status could not be determined due to network, server, or cancellation errors.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The instance is verified to be on the latest build / depot manifest.
    /// </summary>
    UpToDate = 1,

    /// <summary>
    /// A newer build or depot manifest is available.
    /// </summary>
    UpdateAvailable = 2
}
