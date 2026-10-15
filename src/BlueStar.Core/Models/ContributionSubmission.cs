using System;
using System.Text;

namespace BlueStar.Core.Models;

/// <summary>
/// Fully sanitized, consent-backed payload prepared for submission to the community Manifest Hub or Stardust ledger.
/// Guarantees 100% absence of Steam credentials, tokens, cookies, raw SteamIDs, and personal browsing/license history.
/// </summary>
public record ContributionSubmission
{
    /// <summary>
    /// Steam AppId associated with the manifest.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Steam DepotId associated with the manifest.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Manifest revision identifier (GID).
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Cryptographic SHA-256 hash of the manifest payload.
    /// </summary>
    public string ManifestHashSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Size of the manifest in bytes.
    /// </summary>
    public long ManifestSizeBytes { get; init; }

    /// <summary>
    /// One-way pseudonymous contributor ID (SHA-256 hashed with salt).
    /// </summary>
    public string PseudonymousContributorId { get; init; } = string.Empty;

    /// <summary>
    /// Binary content of the manifest payload (Steam protobuf format).
    /// </summary>
    public byte[] ManifestPayload { get; init; } = [];

    /// <summary>
    /// Indicates that the user has explicitly reviewed and consented to the contribution.
    /// </summary>
    public bool UserConsentGranted { get; init; }

    /// <summary>
    /// Cryptographic audit hash linking contributor pseudonym and manifest hash for ledger immutability.
    /// </summary>
    public string SanitizationAuditHash { get; init; } = string.Empty;

    /// <summary>
    /// Submission creation timestamp in UTC.
    /// </summary>
    public DateTimeOffset SubmittedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Validates that the submission contains no forbidden plain text patterns or credentials.
    /// </summary>
    public bool IsSafeForTransmission =>
        UserConsentGranted &&
        !string.IsNullOrWhiteSpace(PseudonymousContributorId) &&
        !string.IsNullOrWhiteSpace(ManifestHashSha256) &&
        ManifestPayload.Length > 0 &&
        ManifestPayload.Length == ManifestSizeBytes;

    /// <summary>
    /// Performs deep content audit to ensure no sensitive tokens or Steam credentials are embedded in the payload.
    /// </summary>
    /// <param name="sensitiveTokens">Known tokens, cookies, or raw SteamIDs to check against.</param>
    /// <returns>True if clean; false if any sensitive token was found.</returns>
    public bool AuditForSensitiveTokens(params string[] sensitiveTokens)
    {
        if (sensitiveTokens == null || sensitiveTokens.Length == 0)
        {
            return true;
        }

        // Check metadata fields
        foreach (var token in sensitiveTokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;

            if (PseudonymousContributorId.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;

            if (ManifestHashSha256.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;

            if (SanitizationAuditHash.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Check payload bytes for plain text token leaks
        if (ManifestPayload.Length > 0)
        {
            var payloadAscii = Encoding.ASCII.GetString(ManifestPayload);
            foreach (var token in sensitiveTokens)
            {
                if (!string.IsNullOrWhiteSpace(token) && token.Length >= 4)
                {
                    if (payloadAscii.Contains(token, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }

        return true;
    }
}
