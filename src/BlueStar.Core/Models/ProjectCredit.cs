namespace BlueStar.Core.Models;

/// <summary>
/// Model representing an open-source project or dependency used in BlueStar.
/// </summary>
public sealed class ProjectCredit
{
    /// <summary>
    /// Project name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Role or responsibility within BlueStar.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Short summary of what the project does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Primary author or organization.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// License type (e.g. GPLv3, MIT, LGPL-2.1, Apache-2.0).
    /// </summary>
    public required string License { get; init; }

    /// <summary>
    /// URL to the GitHub repository.
    /// </summary>
    public required string GitHubUrl { get; init; }

    /// <summary>
    /// Optional website / documentation URL.
    /// </summary>
    public string? WebsiteUrl { get; init; }

    /// <summary>
    /// Category badge (e.g. "Primary", "Emulation", "Modding", "Downloader", "Framework").
    /// </summary>
    public string CategoryBadge { get; init; } = "Library";

    /// <summary>
    /// Whether this is a core primary component of BlueStar.
    /// </summary>
    public bool IsCore { get; init; }

    /// <summary>
    /// Display text for the external link button (e.g. "GitHub ↗", "GitLab ↗", "Website ↗").
    /// </summary>
    public string LinkButtonText =>
        GitHubUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "GitHub ↗" :
        GitHubUrl.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase) ? "GitLab ↗" : "Website ↗";
}
