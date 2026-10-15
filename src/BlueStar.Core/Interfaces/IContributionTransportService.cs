using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Handles secure, consent-driven remote transport of voluntary community contributions
/// to the authorized Manifest Hub and manages Stardust attribution upon verified publication.
/// </summary>
public interface IContributionTransportService
{
    /// <summary>
    /// Validates the binary manifest payload, verifies explicit user consent,
    /// transmits the sanitized contribution to the authorized Hub service,
    /// and idempotently credits Stardust upon confirmed server publication.
    /// </summary>
    /// <param name="submission">The sanitized submission package.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A publication receipt returned by the server confirming acceptance and status.</returns>
    Task<PublicationReceipt> TransmitContributionAsync(ContributionSubmission submission, CancellationToken ct = default);

    /// <summary>
    /// Determines whether a contribution for the specified depot and manifest has already been successfully published and credited.
    /// </summary>
    /// <param name="depotId">The Steam Depot ID.</param>
    /// <param name="manifestId">The Manifest revision ID.</param>
    /// <returns>True if already credited; otherwise, false.</returns>
    bool IsAlreadyCredited(uint depotId, ulong manifestId);

    /// <summary>
    /// Gets the total verified Stardust units credited to this anonymous contributor.
    /// </summary>
    long GetTotalStardustCredited();

    /// <summary>
    /// Fetches the authoritative Stardust point balance directly from the server's ledger endpoint
    /// (GET /api/v1/stardust/balance/{contributorId}), distinguishing confirmed points from pending points.
    /// </summary>
    /// <param name="contributorId">The anonymous contributor UUID v4.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verified Stardust balance information.</returns>
    Task<StardustBalanceInfo> GetStardustBalanceAsync(string contributorId, CancellationToken ct = default);
}
