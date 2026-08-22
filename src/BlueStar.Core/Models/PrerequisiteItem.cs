namespace BlueStar.Core.Models;

/// <summary>
/// Status of a game runtime prerequisite on the system and within the game package.
/// </summary>
public enum PrerequisiteStatus
{
    /// <summary>
    /// The runtime is verified to be installed on the Windows operating system.
    /// </summary>
    InstalledInSystem,

    /// <summary>
    /// The installer is present in the game directory or shared depots ready to run.
    /// </summary>
    AvailableInGame,

    /// <summary>
    /// Not present locally, but available for official automatic download and installation.
    /// </summary>
    NeedsDownload,

    /// <summary>
    /// Currently being installed.
    /// </summary>
    Installing,

    /// <summary>
    /// Successfully installed in current session.
    /// </summary>
    InstalledSuccess,

    /// <summary>
    /// Installation encountered an error.
    /// </summary>
    InstallFailed
}

/// <summary>
/// Represents a game prerequisite / redistributable dependency (e.g. Visual C++, DirectX, .NET, Unreal Prereqs).
/// </summary>
public record PrerequisiteItem
{
    /// <summary>
    /// Unique identifier for this prerequisite.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the prerequisite.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Category of dependency ("Visual C++", "DirectX", ".NET Runtime", "Unreal Engine", "PhysX", "OpenAL", "Driver").
    /// </summary>
    public string Category { get; init; } = "Visual C++";

    /// <summary>
    /// Brief explanation of why this prerequisite is required.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Current installation status on the local system.
    /// </summary>
    public PrerequisiteStatus Status { get; set; } = PrerequisiteStatus.NeedsDownload;

    /// <summary>
    /// Path to the local installer binary if found in game directory or shared tools.
    /// </summary>
    public string? LocalInstallerPath { get; set; }

    /// <summary>
    /// Direct official download URL if needed.
    /// </summary>
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Arguments for silent unattended installation.
    /// </summary>
    public string SilentArguments { get; init; } = "/passive /norestart";

    /// <summary>
    /// Whether this prerequisite is essential for most modern games.
    /// </summary>
    public bool IsEssential { get; init; } = true;
}
