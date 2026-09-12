namespace BlueStar.Core.Models;

/// <summary>
/// How the detected hardware compares to an app's stated system requirements.
/// </summary>
/// <remarks>
/// RAM, operating system and free disk space are compared exactly. CPU and GPU are matched
/// against a relative performance table, so any verdict that depended on them is an estimate.
/// An app whose <c>pc_requirements</c> could not be parsed is <see cref="Unknown"/> and is never
/// filtered out on that basis.
/// </remarks>
public enum RequirementsVerdict
{
    /// <summary>Requirements could not be read, or the check has not run yet.</summary>
    Unknown = 0,

    /// <summary>Below the stated minimum.</summary>
    BelowMinimum = 1,

    /// <summary>Meets the minimum but not the recommended specification.</summary>
    MeetsMinimum = 2,

    /// <summary>Meets or exceeds the recommended specification.</summary>
    MeetsRecommended = 3
}
