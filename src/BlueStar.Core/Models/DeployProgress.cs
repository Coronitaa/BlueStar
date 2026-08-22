namespace BlueStar.Core.Models;

/// <summary>
/// Represents the progress and status of an emulator or tool deployment operation.
/// </summary>
public record DeployProgress
{
    /// <summary>
    /// Gets the percentage of the deployment complete (0 to 100).
    /// </summary>
    public double Percentage { get; init; }

    /// <summary>
    /// Gets the human-readable description of the current action.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets an optional identifier or title for the active step.
    /// </summary>
    public string? CurrentStep { get; init; }
}
