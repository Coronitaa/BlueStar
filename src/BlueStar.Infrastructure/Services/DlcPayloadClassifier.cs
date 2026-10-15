using System;
using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IDlcPayloadClassifier"/>, accurately distinguishing between DLCs
/// that require dedicated depot payloads (e.g. Poppy Playtime episodic chapters) and those
/// that are activated purely through client entitlement or unlock keys (e.g. Tabletop Simulator DLCs).
/// </summary>
public sealed class DlcPayloadClassifier : IDlcPayloadClassifier
{
    private readonly ILogger<DlcPayloadClassifier> _logger;

    public DlcPayloadClassifier(ILogger<DlcPayloadClassifier>? logger = null)
    {
        _logger = logger ?? NullLogger<DlcPayloadClassifier>.Instance;
    }

    /// <inheritdoc />
    public DlcClassification Classify(uint dlcAppId, uint parentAppId, string dlcName, IEnumerable<DepotInfo>? depots)
    {
        if (depots == null)
        {
            _logger.LogDebug("DLC {DlcAppId} ('{DlcName}') has null depot list; classifying as Unknown", dlcAppId, dlcName);
            return new DlcClassification
            {
                DlcAppId = dlcAppId,
                ParentAppId = parentAppId,
                Name = dlcName,
                PayloadType = DlcPayloadType.Unknown,
                DepotIds = [],
                TotalSizeBytes = 0,
                Reason = "No depot metadata available for evaluation."
            };
        }

        var depotList = depots.ToList();
        var depotIds = depotList.Select(d => d.DepotId).Distinct().ToList();
        var totalSize = depotList.Sum(d => d.SizeBytes);

        // Check for explicit external content indicators (e.g. external CDN or account keys)
        if (dlcName.Contains("external", StringComparison.OrdinalIgnoreCase) ||
            depotList.Any(d => d.Category.Equals("External", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("DLC {DlcAppId} ('{DlcName}') classified as ExternalContent", dlcAppId, dlcName);
            return new DlcClassification
            {
                DlcAppId = dlcAppId,
                ParentAppId = parentAppId,
                Name = dlcName,
                PayloadType = DlcPayloadType.ExternalContent,
                DepotIds = depotIds.AsReadOnly(),
                TotalSizeBytes = totalSize,
                Reason = "DLC content is hosted on external infrastructure or third-party servers."
            };
        }

        // EntitlementOnly case 1: No depots exist for this DLC (e.g. Tabletop Simulator DLCs, activation unlock keys)
        if (depotList.Count == 0)
        {
            _logger.LogInformation("DLC {DlcAppId} ('{DlcName}') classified as EntitlementOnly (0 depots)", dlcAppId, dlcName);
            return new DlcClassification
            {
                DlcAppId = dlcAppId,
                ParentAppId = parentAppId,
                Name = dlcName,
                PayloadType = DlcPayloadType.EntitlementOnly,
                DepotIds = [],
                TotalSizeBytes = 0,
                Reason = "DLC has no separate depots; files are pre-shipped in the base game or unlocked via client entitlement (e.g. Tabletop Simulator DLCs)."
            };
        }

        // DownloadablePayload: Has depots with non-zero byte size or valid non-zero manifest revision
        // (e.g. Poppy Playtime Ch 2, 3, 4 which have distinct multi-gigabyte downloadable assets)
        var hasContent = totalSize > 0 || depotList.Any(d => d.ManifestId > 0);
        if (hasContent)
        {
            _logger.LogInformation("DLC {DlcAppId} ('{DlcName}') classified as DownloadablePayload ({DepotCount} depots, {TotalSize} bytes)",
                dlcAppId, dlcName, depotList.Count, totalSize);

            return new DlcClassification
            {
                DlcAppId = dlcAppId,
                ParentAppId = parentAppId,
                Name = dlcName,
                PayloadType = DlcPayloadType.DownloadablePayload,
                DepotIds = depotIds.AsReadOnly(),
                TotalSizeBytes = totalSize,
                Reason = $"Requires download of {depotList.Count} dedicated content depot(s) totaling {totalSize} bytes (e.g. Poppy Playtime episodic payloads)."
            };
        }

        // EntitlementOnly case 2: Has depot references but with 0 size and 0 ManifestId (stub/entitlement marker)
        _logger.LogInformation("DLC {DlcAppId} ('{DlcName}') classified as EntitlementOnly (0-byte stubs)", dlcAppId, dlcName);
        return new DlcClassification
        {
            DlcAppId = dlcAppId,
            ParentAppId = parentAppId,
            Name = dlcName,
            PayloadType = DlcPayloadType.EntitlementOnly,
            DepotIds = depotIds.AsReadOnly(),
            TotalSizeBytes = 0,
            Reason = "Depots represent zero-byte entitlement stubs without downloadable file manifests."
        };
    }

    /// <inheritdoc />
    public DlcClassification Classify(uint parentAppId, DlcInfo dlcInfo)
    {
        ArgumentNullException.ThrowIfNull(dlcInfo);
        return Classify(dlcInfo.AppId, parentAppId, dlcInfo.Name, dlcInfo.Depots);
    }

    /// <inheritdoc />
    public IReadOnlyList<DlcClassification> ClassifyAll(uint parentAppId, IEnumerable<DlcInfo> dlcs)
    {
        ArgumentNullException.ThrowIfNull(dlcs);
        return dlcs.Select(dlc => Classify(parentAppId, dlc)).ToList().AsReadOnly();
    }
}
