using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DepotDownloader;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Downloader;

/// <summary>
/// Manages persistent download state files, enabling downloads to survive BlueStar restarts.
/// State files are stored at: %LocalAppData%/BlueStar/DepotWork/{instanceId}/download_state.json
/// </summary>
public sealed class DownloadStateManager
{
    private readonly ILogger<DownloadStateManager> _logger;
    private readonly string _baseDir;

    public DownloadStateManager(ILogger<DownloadStateManager> logger)
    {
        _logger = logger;
        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "DepotWork");
    }

    /// <summary>Gets the state file path for a given instance.</summary>
    public string GetStateFilePath(Guid instanceId)
        => Path.Combine(_baseDir, instanceId.ToString(), "download_state.json");

    /// <summary>Saves download state for a specific instance.</summary>
    public void SaveState(Guid instanceId, DownloadStateSnapshot state)
    {
        try
        {
            var path = GetStateFilePath(instanceId);
            state.SaveToFile(path);
            _logger.LogDebug("Saved download state for instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save download state for instance {InstanceId}", instanceId);
        }
    }

    /// <summary>Loads download state for a specific instance. Returns null if no state exists.</summary>
    public DownloadStateSnapshot? LoadState(Guid instanceId)
    {
        var path = GetStateFilePath(instanceId);
        var state = DownloadStateSnapshot.LoadFromFile(path);

        if (state != null)
        {
            _logger.LogInformation("Found saved download state for instance {InstanceId}, started at {StartedAt}",
                instanceId, state.StartedAt);
        }

        return state;
    }

    /// <summary>Clears the state file for a specific instance.</summary>
    public void ClearState(Guid instanceId)
    {
        try
        {
            var path = GetStateFilePath(instanceId);
            DownloadStateSnapshot.DeleteFile(path);
            _logger.LogDebug("Cleared download state for instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear download state for instance {InstanceId}", instanceId);
        }
    }

    /// <summary>
    /// Scans for all pending (interrupted) downloads across all instances.
    /// Used at BlueStar startup to discover downloads that need to be resumed.
    /// </summary>
    public IReadOnlyList<(Guid instanceId, DownloadStateSnapshot state)> GetAllPendingDownloads()
    {
        var results = new List<(Guid, DownloadStateSnapshot)>();

        if (!Directory.Exists(_baseDir))
            return results;

        try
        {
            foreach (var dir in Directory.GetDirectories(_baseDir))
            {
                var dirName = Path.GetFileName(dir);
                if (!Guid.TryParse(dirName, out var instanceId))
                    continue;

                var stateFile = Path.Combine(dir, "download_state.json");
                var state = DownloadStateSnapshot.LoadFromFile(stateFile);
                if (state != null)
                {
                    // Check if the download is actually incomplete
                    bool hasIncompleteDepots = state.Depots.Any(d => !d.IsComplete);
                    if (hasIncompleteDepots)
                    {
                        results.Add((instanceId, state));
                        _logger.LogInformation(
                            "Found pending download: Instance {InstanceId}, AppId {AppId}, {Completed}/{Total} depots complete",
                            instanceId, state.AppId,
                            state.Depots.Count(d => d.IsComplete), state.Depots.Count);
                    }
                    else
                    {
                        // All depots complete, clean up stale state file
                        DownloadStateSnapshot.DeleteFile(stateFile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning for pending downloads in {Dir}", _baseDir);
        }

        return results;
    }
}
