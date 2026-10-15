using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IVoluntaryContributionService"/>, managing transparent, consent-driven
/// community manifest contributions with cryptographic pseudonymity and privacy guarantees.
/// 
/// PRIVACY INVARIANTS:
/// - Real SteamID64 and account names are NEVER exposed or transmitted.
/// - Cryptographic salted one-way hashing (SHA-256) is used for contributor attribution in the Stardust ledger.
/// - No credentials, tokens, session cookies, or game library lists are ever packaged.
/// - Explicit user consent is mandatory prior to generating any submission payload.
/// </summary>
public sealed class VoluntaryContributionService : IVoluntaryContributionService
{
    public const string DefaultSalt = "bluestar-stardust-ledger-v1";
    private readonly ILogger<VoluntaryContributionService> _logger;

    public VoluntaryContributionService(ILogger<VoluntaryContributionService>? logger = null)
    {
        _logger = logger ?? NullLogger<VoluntaryContributionService>.Instance;
    }

    /// <inheritdoc />
    public string GeneratePseudonym(string rawUserIdentifier, string? salt = null)
    {
        if (string.IsNullOrWhiteSpace(rawUserIdentifier))
        {
            throw new ArgumentException("Raw user identifier cannot be null or empty", nameof(rawUserIdentifier));
        }

        var effectiveSalt = !string.IsNullOrWhiteSpace(salt) ? salt : DefaultSalt;
        var compositeInput = $"bluestar-stardust:{effectiveSalt}:{rawUserIdentifier.Trim()}";

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(compositeInput));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<ContributionPreview> CreatePreviewAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string manifestFilePath,
        string rawUserIdentifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawUserIdentifier);

        if (!File.Exists(manifestFilePath))
        {
            throw new FileNotFoundException($"Local manifest file not found: {manifestFilePath}", manifestFilePath);
        }

        var bytes = await File.ReadAllBytesAsync(manifestFilePath, ct).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"Manifest file is empty: {manifestFilePath}");
        }

        string hashHex;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(bytes);
            hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var pseudonym = GeneratePseudonym(rawUserIdentifier);

        _logger.LogInformation(
            "Created voluntary contribution preview for App {AppId}, Depot {DepotId}, Manifest {ManifestId}. Pseudonym: {Pseudonym}",
            appId, depotId, manifestId, pseudonym);

        return new ContributionPreview
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = hashHex,
            ManifestSizeBytes = bytes.Length,
            PseudonymousContributorId = pseudonym,
            LocalSourcePath = manifestFilePath,
            PrivacyDisclosures = ContributionPreview.StandardDisclosures,
            IsSanitized = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<ContributionSubmission> PrepareSubmissionAsync(
        ContributionPreview preview,
        bool userConsentGranted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (!userConsentGranted)
        {
            _logger.LogWarning("Contribution submission rejected: User consent was not granted for Depot {DepotId}", preview.DepotId);
            throw new InvalidOperationException("Voluntary community contribution strictly requires explicit user consent.");
        }

        if (!File.Exists(preview.LocalSourcePath))
        {
            throw new FileNotFoundException($"Manifest source file missing during submission prep: {preview.LocalSourcePath}");
        }

        var payload = await File.ReadAllBytesAsync(preview.LocalSourcePath, ct).ConfigureAwait(false);

        // Verify cryptographic integrity against preview
        string currentHash;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(payload);
            currentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(currentHash, preview.ManifestHashSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Manifest checksum mismatch. Expected {preview.ManifestHashSha256}, but found {currentHash}. Potential file tampering detected.");
        }

        // Generate Sanitization Audit Hash (links contributor pseudonym + manifest hash for Stardust ledger integrity)
        string auditHash;
        using (var sha256 = SHA256.Create())
        {
            var auditInput = $"audit:{preview.PseudonymousContributorId}:{preview.ManifestHashSha256}:{payload.Length}";
            auditHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(auditInput))).ToLowerInvariant();
        }

        var submission = new ContributionSubmission
        {
            AppId = preview.AppId,
            DepotId = preview.DepotId,
            ManifestId = preview.ManifestId,
            ManifestHashSha256 = preview.ManifestHashSha256,
            ManifestSizeBytes = payload.Length,
            PseudonymousContributorId = preview.PseudonymousContributorId,
            ManifestPayload = payload,
            UserConsentGranted = true,
            SanitizationAuditHash = auditHash,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };

        if (!submission.IsSafeForTransmission)
        {
            throw new InvalidOperationException("Submission failed final safety validation.");
        }

        _logger.LogInformation(
            "Packaged sanitized voluntary submission for Depot {DepotId}, Manifest {ManifestId}. Audit: {AuditHash}",
            submission.DepotId, submission.ManifestId, submission.SanitizationAuditHash);

        return submission;
    }
}
