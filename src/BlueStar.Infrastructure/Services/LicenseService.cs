using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Default implementation of <see cref="ILicenseService"/>.
/// Prepared for future PRO licensing validation. Currently defaults to Free tier (IsProLicenseActive = false).
/// </summary>
public sealed class LicenseService : ILicenseService
{
    private readonly ILogger<LicenseService> _logger;

    /// <inheritdoc />
    public bool IsProLicenseActive => false; // Not implemented yet per specification

    /// <inheritdoc />
    public string LicenseTierName => IsProLicenseActive ? "PRO" : "Free";

    public LicenseService(ILogger<LicenseService> logger)
    {
        _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> ValidateLicenseAsync(string licenseKey, CancellationToken ct = default)
    {
        // Future PRO license validation stub
        _logger.LogInformation("License validation requested (stub)");
        return Task.FromResult(false);
    }
}
