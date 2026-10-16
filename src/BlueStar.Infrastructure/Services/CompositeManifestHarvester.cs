using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Composite harvester that cascades through all available <see cref="IManifestHarvesterProvider"/>
/// instances in priority order until a valid manifest is captured.
/// </summary>
public sealed class CompositeManifestHarvester : IManifestHarvestCoordinator, IManifestHarvesterProvider
{
    private readonly List<IManifestHarvesterProvider> _providers;
    private readonly ILogger<CompositeManifestHarvester> _logger;

    public string Name => "CompositeManifestHarvester";
    public int Priority => 1000;

    public IReadOnlyList<IManifestHarvesterProvider> Providers => _providers.AsReadOnly();

    public CompositeManifestHarvester(
        IEnumerable<IManifestHarvesterProvider> providers,
        ILogger<CompositeManifestHarvester>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.OrderByDescending(p => p.Priority).ToList();
        _logger = logger ?? NullLogger<CompositeManifestHarvester>.Instance;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        foreach (var p in _providers)
        {
            if (await p.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default)
    {
        var errors = new List<string>();

        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var isAvailable = await provider.IsAvailableAsync(ct).ConfigureAwait(false);
                if (!isAvailable)
                {
                    _logger.LogDebug("Harvester provider {Provider} is not available, skipping", provider.Name);
                    continue;
                }

                _logger.LogInformation("Attempting harvest with provider {Provider} for Depot {DepotId}, Manifest {ManifestId}",
                    provider.Name, depotId, manifestId);

                var result = await provider.HarvestManifestAsync(appId, depotId, manifestId, ct).ConfigureAwait(false);
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.ManifestFilePath))
                {
                    _logger.LogInformation("Successfully harvested manifest {DepotId}_{ManifestId} via provider {Provider}",
                        depotId, manifestId, provider.Name);
                    return result;
                }

                errors.Add($"{provider.Name}: {result.ErrorMessage ?? "failed"}");
                _logger.LogWarning("Harvester provider {Provider} failed: {Error}", provider.Name, result.ErrorMessage);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{provider.Name}: {ex.Message}");
                _logger.LogWarning(ex, "Harvester provider {Provider} threw an unhandled exception", provider.Name);
            }
        }

        var combinedError = errors.Count > 0
            ? string.Join("; ", errors)
            : "No manifest harvester providers were available.";

        return HarvestResult.Failed($"All harvester providers failed to capture manifest {depotId}_{manifestId}. {combinedError}", Name);
    }
}
