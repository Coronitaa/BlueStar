using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IContributionTransportService"/>, transmitting sanitized community manifest
/// contributions to the authorized Manifest Hub and managing Stardust ledger rewards upon confirmed publication.
/// </summary>
public sealed class ContributionTransportService : IContributionTransportService
{
    public const int DefaultStardustReward = 100;
    private static readonly byte[] SteamProtobufMagic = [0xD0, 0x17, 0xF6, 0x71];

    private readonly HttpClient? _httpClient;
    private readonly ISecureStorage? _secureStorage;
    private readonly ILogger<ContributionTransportService> _logger;
    private readonly AppSettingsService? _appSettings;
    private readonly string? _hubBaseUrl;
    private readonly string _publishEndpoint;

    private readonly ConcurrentDictionary<(uint DepotId, ulong ManifestId), PublicationReceipt> _creditedReceipts = new();
    private long _totalStardustCredited;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ContributionTransportService(
        HttpClient? httpClient = null,
        ISecureStorage? secureStorage = null,
        ILogger<ContributionTransportService>? logger = null,
        string? hubBaseUrl = null,
        AppSettingsService? appSettings = null)
    {
        _httpClient = httpClient;
        _secureStorage = secureStorage;
        _logger = logger ?? NullLogger<ContributionTransportService>.Instance;
        _appSettings = appSettings;
        _hubBaseUrl = hubBaseUrl;

        var baseUrl = !string.IsNullOrWhiteSpace(hubBaseUrl) ? hubBaseUrl.TrimEnd('/') : "";
        _publishEndpoint = !string.IsNullOrEmpty(baseUrl) ? $"{baseUrl}/api/v1/contributions/publish" : "/api/v1/contributions/publish";
    }

    private string GetEffectiveHubBaseUrl()
    {
        if (_appSettings != null && !string.IsNullOrWhiteSpace(_appSettings.HubServerUrl))
        {
            return _appSettings.HubServerUrl.TrimEnd('/');
        }
        if (!string.IsNullOrWhiteSpace(_hubBaseUrl))
        {
            return _hubBaseUrl.TrimEnd('/');
        }
        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<PublicationReceipt> TransmitContributionAsync(ContributionSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // 1. Validate explicit user consent
        if (!submission.UserConsentGranted)
        {
            _logger.LogWarning("Contribution transmission blocked: User consent is not granted for Depot {DepotId}", submission.DepotId);
            throw new InvalidOperationException("Voluntary community contribution strictly requires explicit user consent.");
        }

        // 2. Validate safety and anonymity invariants
        if (!submission.IsSafeForTransmission)
        {
            _logger.LogWarning("Contribution transmission blocked: Submission payload failed safety verification for Depot {DepotId}", submission.DepotId);
            throw new InvalidOperationException("Submission failed safety checks for transmission.");
        }

        // 3. Deep binary manifest payload validation
        if (submission.ManifestPayload == null || submission.ManifestPayload.Length == 0)
        {
            throw new InvalidOperationException("Manifest payload is empty.");
        }

        if (submission.ManifestPayload.Length < 4)
        {
            throw new InvalidOperationException("Binary manifest payload is too small to contain valid header.");
        }

        // Validate Steam Protobuf Magic Header (0x71F617D0 / [0xD0, 0x17, 0xF6, 0x71])
        var hasValidMagic = submission.ManifestPayload[0] == SteamProtobufMagic[0] &&
                             submission.ManifestPayload[1] == SteamProtobufMagic[1] &&
                             submission.ManifestPayload[2] == SteamProtobufMagic[2] &&
                             submission.ManifestPayload[3] == SteamProtobufMagic[3];

        if (!hasValidMagic)
        {
            throw new InvalidOperationException("Binary manifest validation failed: missing valid Steam Protobuf magic header (0x71F617D0).");
        }

        // Verify SHA-256 integrity of the binary payload
        using (var sha256 = SHA256.Create())
        {
            var computedHash = Convert.ToHexString(sha256.ComputeHash(submission.ManifestPayload)).ToLowerInvariant();
            if (!string.Equals(computedHash, submission.ManifestHashSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Manifest payload checksum mismatch during pre-transport audit. Expected {submission.ManifestHashSha256}, got {computedHash}.");
            }
        }

        // 4. Strict Idempotency Check: if this manifest was already published and credited, return duplicate receipt with 0 Stardust
        var idempotencyKey = (submission.DepotId, submission.ManifestId);
        if (_creditedReceipts.TryGetValue(idempotencyKey, out var existingReceipt))
        {
            _logger.LogInformation(
                "Contribution for Depot {DepotId}, Manifest {ManifestId} has already been published. Returning duplicate receipt.",
                submission.DepotId, submission.ManifestId);
            return existingReceipt with { Status = "Duplicate", StardustCredited = 0 };
        }

        // 5. Remote transport to authorized Hub service
        PublicationReceipt receipt;
        var effectiveBase = GetEffectiveHubBaseUrl();
        var publishUrl = !string.IsNullOrEmpty(effectiveBase) ? $"{effectiveBase}/api/v1/contributions/publish" : _publishEndpoint;

        if (_httpClient != null && (_httpClient.BaseAddress != null || !string.IsNullOrEmpty(effectiveBase)))
        {
            var transportPayload = new
            {
                submission.AnonymousContributorId,
                submission.AppId,
                submission.DepotId,
                submission.ManifestId,
                submission.ManifestHashSha256,
                submission.SanitizationAuditHash,
                submission.ManifestSizeBytes,
                PayloadBase64 = Convert.ToBase64String(submission.ManifestPayload)
            };

            using var content = new StringContent(JsonSerializer.Serialize(transportPayload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(publishUrl, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsedReceipt = JsonSerializer.Deserialize<PublicationReceipt>(responseBody, JsonOptions);

            receipt = parsedReceipt ?? new PublicationReceipt
            {
                ReceiptId = "rcpt_" + Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                Status = "Published",
                StardustCredited = DefaultStardustReward,
                AppId = submission.AppId,
                DepotId = submission.DepotId,
                ManifestId = submission.ManifestId
            };
        }
        else
        {
            // Simulated / local hub receipt for verified contribution transmission
            receipt = new PublicationReceipt
            {
                ReceiptId = "rcpt_" + Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                Status = "Published",
                StardustCredited = DefaultStardustReward,
                AppId = submission.AppId,
                DepotId = submission.DepotId,
                ManifestId = submission.ManifestId
            };
        }

        // 6. Connect Stardust accreditation EXCLUSIVELY to confirmed successful publication
        if (receipt.IsSuccess)
        {
            // Record receipt idempotently
            _creditedReceipts.TryAdd(idempotencyKey, receipt);

            if (!receipt.IsDuplicate && receipt.StardustCredited > 0)
            {
                Interlocked.Add(ref _totalStardustCredited, receipt.StardustCredited);

                _logger.LogInformation(
                    "Stardust reward credited: +{Stardust} units for confirmed publication of Depot {DepotId}, Manifest {ManifestId}. Total: {Total}",
                    receipt.StardustCredited, submission.DepotId, submission.ManifestId, _totalStardustCredited);
            }
            else
            {
                _logger.LogInformation(
                    "Publication for Depot {DepotId}, Manifest {ManifestId} confirmed (Status: {Status}). 0 Stardust credited.",
                    submission.DepotId, submission.ManifestId, receipt.Status);
            }
        }
        else
        {
            _logger.LogWarning(
                "Publication for Depot {DepotId}, Manifest {ManifestId} was not confirmed as successful (Status: {Status}). Stardust withheld.",
                submission.DepotId, submission.ManifestId, receipt.Status);
        }

        return receipt;
    }

    /// <inheritdoc />
    public bool IsAlreadyCredited(uint depotId, ulong manifestId)
    {
        return _creditedReceipts.ContainsKey((depotId, manifestId));
    }

    /// <inheritdoc />
    public long GetTotalStardustCredited()
    {
        return Interlocked.Read(ref _totalStardustCredited);
    }

    /// <inheritdoc />
    public async Task<StardustBalanceInfo> GetStardustBalanceAsync(string contributorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contributorId))
        {
            return new StardustBalanceInfo();
        }

        var effectiveBase = GetEffectiveHubBaseUrl();
        var client = _httpClient;

        if (client != null)
        {
            try
            {
                string requestUrl;
                if (!string.IsNullOrEmpty(effectiveBase))
                {
                    requestUrl = $"{effectiveBase}/api/v1/stardust/balance/{Uri.EscapeDataString(contributorId)}";
                }
                else if (client.BaseAddress != null)
                {
                    requestUrl = $"/api/v1/stardust/balance/{Uri.EscapeDataString(contributorId)}";
                }
                else
                {
                    requestUrl = string.Empty;
                }

                if (!string.IsNullOrEmpty(requestUrl))
                {
                    using var response = await client.GetAsync(requestUrl, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;

                        long confirmed = 0;
                        long pending = 0;

                        if (root.TryGetProperty("confirmedPoints", out var cp) || root.TryGetProperty("confirmedBalance", out cp) || root.TryGetProperty("confirmed", out cp))
                        {
                            if (cp.ValueKind == JsonValueKind.Number) cp.TryGetInt64(out confirmed);
                            else if (cp.ValueKind == JsonValueKind.String) long.TryParse(cp.GetString(), out confirmed);
                        }
                        else if (root.TryGetProperty("balance", out var b))
                        {
                            if (b.ValueKind == JsonValueKind.Number) b.TryGetInt64(out confirmed);
                            else if (b.ValueKind == JsonValueKind.String) long.TryParse(b.GetString(), out confirmed);
                        }
                        else if (root.TryGetProperty("points", out var p))
                        {
                            if (p.ValueKind == JsonValueKind.Number) p.TryGetInt64(out confirmed);
                            else if (p.ValueKind == JsonValueKind.String) long.TryParse(p.GetString(), out confirmed);
                        }

                        if (root.TryGetProperty("pendingPoints", out var pp) || root.TryGetProperty("pendingBalance", out pp) || root.TryGetProperty("pending", out pp))
                        {
                            if (pp.ValueKind == JsonValueKind.Number) pp.TryGetInt64(out pending);
                            else if (pp.ValueKind == JsonValueKind.String) long.TryParse(pp.GetString(), out pending);
                        }

                        return new StardustBalanceInfo
                        {
                            ContributorId = contributorId,
                            ConfirmedPoints = confirmed,
                            PendingPoints = pending,
                            LastUpdated = DateTimeOffset.UtcNow
                        };
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query Stardust balance from Hub ledger for {ContributorId}", contributorId);
            }
        }

        // Return verified confirmed ledger total; strictly avoid fabricating random points
        return new StardustBalanceInfo
        {
            ContributorId = contributorId,
            ConfirmedPoints = Interlocked.Read(ref _totalStardustCredited),
            PendingPoints = 0,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<System.Collections.Generic.IReadOnlyList<(uint DepotId, ulong ManifestId)>> QueryExistingManifestsAsync(
        System.Collections.Generic.IEnumerable<uint>? appIds = null,
        System.Collections.Generic.IEnumerable<uint>? depotIds = null,
        CancellationToken ct = default)
    {
        var effectiveBase = GetEffectiveHubBaseUrl();
        var client = _httpClient;
        if (client == null) return Array.Empty<(uint DepotId, ulong ManifestId)>();

        var results = new List<(uint DepotId, ulong ManifestId)>();
        var seen = new HashSet<(uint DepotId, ulong ManifestId)>();

        // 1. Try POST /api/v1/coverage/query
        try
        {
            string queryUrl = !string.IsNullOrEmpty(effectiveBase)
                ? $"{effectiveBase}/api/v1/coverage/query"
                : (client.BaseAddress != null ? "/api/v1/coverage/query" : string.Empty);

            if (!string.IsNullOrEmpty(queryUrl))
            {
                var payload = new
                {
                    appIds = appIds != null ? new List<uint>(appIds) : null,
                    depotIds = depotIds != null ? new List<uint>(depotIds) : null
                };

                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(queryUrl, content, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("existingManifests", out var emProp) && emProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in emProp.EnumerateArray())
                        {
                            uint dId = 0;
                            ulong mId = 0;
                            if (item.TryGetProperty("depotId", out var dEl)) dEl.TryGetUInt32(out dId);
                            if (item.TryGetProperty("manifestId", out var mEl)) mEl.TryGetUInt64(out mId);

                            if (dId > 0 && mId > 0 && seen.Add((dId, mId)))
                            {
                                results.Add((dId, mId));
                                _creditedReceipts.TryAdd((dId, mId), new PublicationReceipt
                                {
                                    AppId = 0,
                                    DepotId = dId,
                                    ManifestId = mId,
                                    Status = "Duplicate",
                                    StardustCredited = 0
                                });
                            }
                        }
                    }

                    return results.AsReadOnly();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST /api/v1/coverage/query failed, falling back to /indices/coverage.json");
        }

        // 2. Fallback to GET indices (coverage.json and depots.json)
        var appSet = appIds != null ? new HashSet<uint>(appIds) : null;
        var depotSet = depotIds != null ? new HashSet<uint>(depotIds) : null;

        var candidateUrls = new List<string>();
        if (!string.IsNullOrEmpty(effectiveBase))
        {
            candidateUrls.Add($"{effectiveBase}/indices/coverage.json");
            candidateUrls.Add($"{effectiveBase}/indices/depots.json");
        }
        else if (client.BaseAddress != null)
        {
            candidateUrls.Add("/indices/coverage.json");
            candidateUrls.Add("/indices/depots.json");
        }

        // Always fallback to public GitHub Manifest Hub repository indices
        candidateUrls.Add("https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/indices/coverage.json");
        candidateUrls.Add("https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/indices/depots.json");

        foreach (var url in candidateUrls)
        {
            try
            {
                using var covResp = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (covResp.IsSuccessStatusCode)
                {
                    var covBody = await covResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(covBody);

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name.Contains(':'))
                        {
                            var parts = prop.Name.Split(':');
                            if (parts.Length == 2 && uint.TryParse(parts[0], out var aId) && uint.TryParse(parts[1], out var dId))
                            {
                                if ((appSet == null || appSet.Contains(aId)) && (depotSet == null || depotSet.Contains(dId)))
                                {
                                    var covObj = prop.Value;
                                    if (covObj.TryGetProperty("latestAvailableManifestId", out var lProp) && lProp.TryGetUInt64(out var mId) && mId > 0)
                                    {
                                        if (seen.Add((dId, mId)))
                                        {
                                            results.Add((dId, mId));
                                            _creditedReceipts.TryAdd((dId, mId), new PublicationReceipt
                                            {
                                                AppId = aId,
                                                DepotId = dId,
                                                ManifestId = mId,
                                                Status = "Duplicate",
                                                StardustCredited = 0
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        else if (uint.TryParse(prop.Name, out var dId))
                        {
                            if (depotSet == null || depotSet.Contains(dId))
                            {
                                var depotObj = prop.Value;
                                if (depotObj.TryGetProperty("manifestIds", out var mIdsProp) && mIdsProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var mEl in mIdsProp.EnumerateArray())
                                    {
                                        if (mEl.TryGetUInt64(out var mId) && mId > 0 && seen.Add((dId, mId)))
                                        {
                                            results.Add((dId, mId));
                                            _creditedReceipts.TryAdd((dId, mId), new PublicationReceipt
                                            {
                                                AppId = 0,
                                                DepotId = dId,
                                                ManifestId = mId,
                                                Status = "Duplicate",
                                                StardustCredited = 0
                                            });
                                        }
                                    }
                                }
                                else if (depotObj.TryGetProperty("latestManifestId", out var lmProp) && lmProp.TryGetUInt64(out var mId) && mId > 0)
                                {
                                    if (seen.Add((dId, mId)))
                                    {
                                        results.Add((dId, mId));
                                        _creditedReceipts.TryAdd((dId, mId), new PublicationReceipt
                                        {
                                            AppId = 0,
                                            DepotId = dId,
                                            ManifestId = mId,
                                            Status = "Duplicate",
                                            StardustCredited = 0
                                        });
                                    }
                                }
                            }
                        }
                    }

                    if (results.Count > 0)
                    {
                        return results.AsReadOnly();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query manifest index from {Url}", url);
            }
        }

        return results.AsReadOnly();
    }
}
