using System.Collections.Generic;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Classifies game DLCs into downloadable payloads vs entitlement-only unlock items.
/// </summary>
public interface IDlcPayloadClassifier
{
    /// <summary>
    /// Classifies an individual DLC based on its depot definitions and payload sizes.
    /// If depots are empty and metadata is not enriched, returns Unknown until evidence is verified.
    /// </summary>
    /// <param name="dlcAppId">The Steam App ID of the DLC.</param>
    /// <param name="parentAppId">The Steam App ID of the base game.</param>
    /// <param name="dlcName">The friendly title of the DLC.</param>
    /// <param name="depots">The depots associated with this DLC.</param>
    /// <param name="isMetadataEnriched">Whether metadata has been enriched via store or manifest inspection.</param>
    /// <param name="hasEntitlementEvidence">Whether confirmed evidence exists that no downloadable payload is needed.</param>
    /// <returns>A classification result detailing whether content download is required.</returns>
    DlcClassification Classify(
        uint dlcAppId,
        uint parentAppId,
        string dlcName,
        IEnumerable<DepotInfo>? depots,
        bool isMetadataEnriched = false,
        bool hasEntitlementEvidence = false);

    /// <summary>
    /// Classifies an individual DLC based on its depot definitions and payload sizes.
    /// </summary>
    DlcClassification Classify(uint dlcAppId, uint parentAppId, string dlcName, IEnumerable<DepotInfo>? depots);

    /// <summary>
    /// Classifies a <see cref="DlcInfo"/> model.
    /// </summary>
    /// <param name="parentAppId">The Steam App ID of the base game.</param>
    /// <param name="dlcInfo">The DLC information model.</param>
    /// <returns>A classification result.</returns>
    DlcClassification Classify(uint parentAppId, DlcInfo dlcInfo);

    /// <summary>
    /// Classifies a collection of DLCs for a parent application.
    /// </summary>
    /// <param name="parentAppId">The Steam App ID of the base game.</param>
    /// <param name="dlcs">The collection of DLCs.</param>
    /// <returns>A read-only list of classifications.</returns>
    IReadOnlyList<DlcClassification> ClassifyAll(uint parentAppId, IEnumerable<DlcInfo> dlcs);
}
