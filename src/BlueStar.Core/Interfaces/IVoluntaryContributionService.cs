using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Manages privacy-preserving voluntary manifest contributions to the community hub and Stardust ledger.
/// Strictly enforces that all submissions are user-consented, cryptographically anonymous,
/// and sanitized of any personal credentials, session tokens, or account history.
/// </summary>
public interface IVoluntaryContributionService
{
    /// <summary>
    /// Gets or creates a persistent, completely anonymous contributor identifier (cryptographic UUID v4).
    /// Stored locally on the user's client with ZERO algorithmic or mathematical derivation from
    /// SteamID, account name, or license lists.
    /// </summary>
    Task<string> GetOrCreateAnonymousContributorIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Synchronously retrieves or generates the anonymous contributor UUID v4.
    /// </summary>
    string GetOrCreateAnonymousContributorId();

    /// <summary>
    /// Generates a <see cref="ContributionPreview"/> summarizing the manifest metadata, SHA-256 checksum,
    /// file size, anonymous contributor ID, and mandatory transparency disclosures for user inspection and consent.
    /// </summary>
    /// <param name="appId">The Steam App ID.</param>
    /// <param name="depotId">The Steam Depot ID.</param>
    /// <param name="manifestId">The Manifest revision ID (GID).</param>
    /// <param name="manifestFilePath">Local file path to the verified .manifest file.</param>
    /// <param name="anonymousContributorId">Optional pre-generated anonymous UUID; if omitted, retrieved/generated automatically.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A contribution preview ready for presentation to the user.</returns>
    Task<ContributionPreview> CreatePreviewAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string manifestFilePath,
        string? anonymousContributorId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies explicit user consent, audits the payload for absence of credentials,
    /// and packages the manifest into a sanitized <see cref="ContributionSubmission"/>.
    /// </summary>
    /// <param name="preview">The previously inspected and accepted preview.</param>
    /// <param name="userConsentGranted">Whether the user has explicitly consented to this contribution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully sanitized submission ready for transmission to the community archive.</returns>
    Task<ContributionSubmission> PrepareSubmissionAsync(
        ContributionPreview preview,
        bool userConsentGranted,
        CancellationToken ct = default);

    /// <summary>
    /// Matches the user's legitimately owned Steam App IDs against community demand records,
    /// identifying manifests that the user is authorized to contribute.
    /// </summary>
    IReadOnlyList<DemandRecord> MatchDemandWithLicenses(
        IEnumerable<uint> ownedAppIds,
        DemandIndex demandIndex);
}
