using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Manages privacy-preserving voluntary manifest contributions to the community hub and Stardust ledger.
/// Strictly enforces that all submissions are user-consented, cryptographically pseudonymous,
/// and sanitized of any personal credentials, session tokens, or account history.
/// </summary>
public interface IVoluntaryContributionService
{
    /// <summary>
    /// Generates a deterministic, irreversible pseudonymous identifier for a contributor
    /// using a cryptographic one-way salted hash (SHA-256).
    /// The user's real SteamID or account name is never exposed or stored in cleartext.
    /// </summary>
    /// <param name="rawUserIdentifier">The user's local identifier (e.g., SteamID64 or local user ID).</param>
    /// <param name="salt">Optional application salt; if omitted, the default secure salt is applied.</param>
    /// <returns>A 64-character lowercase hex string representing the contributor pseudonym.</returns>
    string GeneratePseudonym(string rawUserIdentifier, string? salt = null);

    /// <summary>
    /// Generates a <see cref="ContributionPreview"/> summarizing the manifest metadata, SHA-256 checksum,
    /// file size, pseudonymous ID, and mandatory transparency disclosures for user inspection and consent.
    /// </summary>
    /// <param name="appId">The Steam App ID.</param>
    /// <param name="depotId">The Steam Depot ID.</param>
    /// <param name="manifestId">The Manifest revision ID (GID).</param>
    /// <param name="manifestFilePath">Local file path to the verified .manifest file.</param>
    /// <param name="rawUserIdentifier">The user's local identifier (will be converted to pseudonym).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A contribution preview ready for presentation to the user.</returns>
    Task<ContributionPreview> CreatePreviewAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string manifestFilePath,
        string rawUserIdentifier,
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
}
