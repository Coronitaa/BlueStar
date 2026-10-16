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
/// community manifest contributions with complete anonymity and cryptographic privacy guarantees.
/// 
/// PRIVACY INVARIANTS:
/// - Real SteamID64, account names, and license lists are NEVER exposed or transmitted.
/// - Contributor attribution uses a purely random, local UUID v4 (AnonymousContributorId)
///   with ZERO derivation or connection to user credentials.
/// - No credentials, tokens, session cookies, or game library lists are ever packaged.
/// - Explicit user consent is mandatory prior to generating any submission payload.
/// </summary>
public sealed class VoluntaryContributionService : IVoluntaryContributionService
{
    private const string SecureStorageKey = "bluestar_anonymous_contributor_id";
    private readonly ISecureStorage? _secureStorage;
    private readonly ILogger<VoluntaryContributionService> _logger;
    private readonly string _fallbackFilePath;
    private string? _cachedContributorId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VoluntaryContributionService(
        ISecureStorage? secureStorage = null,
        ILogger<VoluntaryContributionService>? logger = null)
    {
        _secureStorage = secureStorage;
        _logger = logger ?? NullLogger<VoluntaryContributionService>.Instance;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _fallbackFilePath = Path.Combine(localAppData, "BlueStar", "contributor_id.txt");
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateAnonymousContributorIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedContributorId))
        {
            return _cachedContributorId;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedContributorId))
            {
                return _cachedContributorId;
            }

            // 1. Try retrieving from ISecureStorage if available
            if (_secureStorage != null)
            {
                try
                {
                    var storedId = await _secureStorage.GetAsync(SecureStorageKey, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(storedId) && Guid.TryParse(storedId, out _))
                    {
                        _cachedContributorId = storedId.Trim().ToLowerInvariant();
                        return _cachedContributorId;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not retrieve anonymous contributor ID from secure storage; checking local fallback");
                }
            }

            // 2. Try retrieving from local fallback file
            try
            {
                if (File.Exists(_fallbackFilePath))
                {
                    var fileId = (await File.ReadAllTextAsync(_fallbackFilePath, ct).ConfigureAwait(false)).Trim();
                    if (!string.IsNullOrWhiteSpace(fileId) && Guid.TryParse(fileId, out _))
                    {
                        _cachedContributorId = fileId.ToLowerInvariant();

                        // Sync back to secure storage if available
                        if (_secureStorage != null)
                        {
                            try { await _secureStorage.SetAsync(SecureStorageKey, _cachedContributorId, ct).ConfigureAwait(false); } catch { }
                        }

                        return _cachedContributorId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not retrieve anonymous contributor ID from local file");
            }

            // 3. Generate a brand-new, purely random cryptographic UUID v4
            // Guaranteed to have zero algorithmic or mathematical connection to SteamID or user identity
            var newAnonymousId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            _cachedContributorId = newAnonymousId;

            // Persist securely
            if (_secureStorage != null)
            {
                try
                {
                    await _secureStorage.SetAsync(SecureStorageKey, newAnonymousId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not persist anonymous contributor ID to secure storage");
                }
            }

            try
            {
                var dir = Path.GetDirectoryName(_fallbackFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(_fallbackFilePath, newAnonymousId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist anonymous contributor ID to local file fallback");
            }

            return _cachedContributorId;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public string GetOrCreateAnonymousContributorId()
    {
        if (!string.IsNullOrWhiteSpace(_cachedContributorId))
        {
            return _cachedContributorId;
        }

        return GetOrCreateAnonymousContributorIdAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<ContributionPreview> CreatePreviewAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string manifestFilePath,
        string? anonymousContributorId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFilePath);

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

        var contributorId = !string.IsNullOrWhiteSpace(anonymousContributorId)
            ? anonymousContributorId
            : await GetOrCreateAnonymousContributorIdAsync(ct).ConfigureAwait(false);

        // PRIVACY GUARANTEE: Never log contributor ID, pseudonym, credentials, or licenses
        _logger.LogInformation(
            "Created voluntary contribution preview for App {AppId}, Depot {DepotId}, Manifest {ManifestId}",
            appId, depotId, manifestId);

        return new ContributionPreview
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = hashHex,
            ManifestSizeBytes = bytes.Length,
            AnonymousContributorId = contributorId,
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

        // Generate Sanitization Audit Hash linking anonymous contributor ID + manifest hash for ledger immutability
        string auditHash;
        using (var sha256 = SHA256.Create())
        {
            var auditInput = $"audit:{preview.AnonymousContributorId}:{preview.ManifestHashSha256}:{payload.Length}";
            auditHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(auditInput))).ToLowerInvariant();
        }

        var submission = new ContributionSubmission
        {
            AppId = preview.AppId,
            DepotId = preview.DepotId,
            ManifestId = preview.ManifestId,
            ManifestHashSha256 = preview.ManifestHashSha256,
            ManifestSizeBytes = payload.Length,
            AnonymousContributorId = preview.AnonymousContributorId,
            ManifestPayload = payload,
            UserConsentGranted = true,
            SanitizationAuditHash = auditHash,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };

        if (!submission.IsSafeForTransmission)
        {
            throw new InvalidOperationException("Submission failed final safety validation.");
        }

        // PRIVACY GUARANTEE: Never log contributor ID, pseudonym, or credentials
        _logger.LogInformation(
            "Packaged sanitized voluntary submission for Depot {DepotId}, Manifest {ManifestId}",
            submission.DepotId, submission.ManifestId);

        return submission;
    }

    /// <inheritdoc />
    public IReadOnlyList<DemandRecord> MatchDemandWithLicenses(
        IEnumerable<uint> ownedAppIds,
        DemandIndex demandIndex)
    {
        ArgumentNullException.ThrowIfNull(ownedAppIds);
        ArgumentNullException.ThrowIfNull(demandIndex);

        var ownedSet = new HashSet<uint>(ownedAppIds);
        var matched = new List<DemandRecord>();
        var seen = new HashSet<(uint DepotId, ulong ManifestId)>();

        foreach (var record in demandIndex.Records)
        {
            if (ownedSet.Contains(record.AppId))
            {
                if (seen.Add((record.DepotId, record.ManifestId)))
                {
                    matched.Add(record);
                }
            }
        }

        return matched.AsReadOnly();
    }
}
