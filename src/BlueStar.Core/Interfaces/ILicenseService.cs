using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service contract for license validation and tier management.
/// </summary>
public interface ILicenseService
{
    /// <summary>
    /// Gets whether the current user has an active PRO license.
    /// </summary>
    bool IsProLicenseActive { get; }

    /// <summary>
    /// Gets the current license tier display name ("Free" or "PRO").
    /// </summary>
    string LicenseTierName { get; }

    /// <summary>
    /// Validates an optional license key against the licensing backend (future implementation).
    /// </summary>
    Task<bool> ValidateLicenseAsync(string licenseKey, CancellationToken ct = default);
}
