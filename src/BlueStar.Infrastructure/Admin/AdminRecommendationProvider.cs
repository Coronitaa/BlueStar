using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Admin;

/// <summary>
/// Puts the admin's first preset for a game in front of the community curation, so the existing
/// "Recommended" install mode and build resolver pick it up without knowing about presets.
/// </summary>
public sealed class AdminRecommendationProvider : IRecommendationProvider
{
    private readonly IAdminCatalogService _catalog;
    private readonly IRecommendationProvider? _fallback;

    public AdminRecommendationProvider(IAdminCatalogService catalog, IRecommendationProvider? fallback = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _fallback = fallback;
    }

    public string ProviderId => "bluestar_admin";
    public string DisplayName => "BlueStar recommended";
    public int Priority => 200;
    public ProviderCapabilities Capabilities => ProviderCapabilities.CurationAdvice;

    public async Task<CurationRecommendation?> GetRecommendationAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;
        var presets = await _catalog.GetPresetsAsync(appId, ct).ConfigureAwait(false);
        if (presets.Count > 0) return presets[0].ToRecommendation(appId);
        return _fallback == null ? null : await _fallback.GetRecommendationAsync(appId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<uint, CurationRecommendation>> BatchGetRecommendationsAsync(IEnumerable<uint> appIds, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, CurationRecommendation>();
        foreach (var appId in appIds)
        {
            var rec = await GetRecommendationAsync(appId, ct).ConfigureAwait(false);
            if (rec != null) result[appId] = rec;
        }
        return result;
    }
}
