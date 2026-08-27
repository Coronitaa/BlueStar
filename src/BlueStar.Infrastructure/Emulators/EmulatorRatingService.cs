using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Service managing persistent global community ratings and votes for emulation options.
/// </summary>
public sealed class EmulatorRatingService : IEmulatorRatingService
{
    private readonly HttpClient _http;
    private readonly string _workerBaseUrl;
    private readonly ILogger<EmulatorRatingService> _logger;
    private readonly string _ratingsFilePath;
    private readonly string _userVotesFilePath;
    private readonly ConcurrentDictionary<string, RatingData> _ratings = new();
    private readonly ConcurrentDictionary<string, bool> _userVotes = new();
    private readonly ConcurrentDictionary<string, bool> _resetEmulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private class RatingData
    {
        [System.Text.Json.Serialization.JsonPropertyName("positive")]
        public int Positive { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("negative")]
        public int Negative { get; set; }
    }

    public EmulatorRatingService(ILogger<EmulatorRatingService> logger, HttpClient? http = null, string? dataDirectory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        _workerBaseUrl = Environment.GetEnvironmentVariable("BLUESTAR_RATINGS_URL") ?? "https://bluestar-emulator-ratings.blustar.workers.dev";

        var appData = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar");
        Directory.CreateDirectory(appData);

        _ratingsFilePath = Path.Combine(appData, "emulator_ratings.json");
        _userVotesFilePath = Path.Combine(appData, "user_votes.json");

        LoadData();
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_ratingsFilePath))
            {
                var json = File.ReadAllText(_ratingsFilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, RatingData>>(json);
                if (loaded != null)
                {
                    foreach (var kvp in loaded)
                    {
                        _ratings[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (File.Exists(_userVotesFilePath))
            {
                var json = File.ReadAllText(_userVotesFilePath);
                var loadedVotes = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (loadedVotes != null)
                {
                    foreach (var kvp in loadedVotes)
                    {
                        _userVotes[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load emulator ratings file, using memory store.");
        }
    }

    private async Task SaveDataAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ratingsJson = JsonSerializer.Serialize(_ratings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_ratingsFilePath, ratingsJson, ct).ConfigureAwait(false);

            var votesJson = JsonSerializer.Serialize(_userVotes, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_userVotesFilePath, votesJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist emulator ratings.");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmulatorOptionInfo>> GetOptionsForInstanceAsync(GameInstance instance, CancellationToken ct = default)
    {
        // 1. Try to sync ratings from Cloudflare Worker
        await FetchRemoteRatingsAsync(instance.AppId, ct).ConfigureAwait(false);

        var isUnreal = instance.Engine?.Type == EngineType.UnrealEngine;
        var isGodot = instance.Engine?.Type == EngineType.Godot;
        var isUnity = instance.Engine?.Type == EngineType.Unity;

        var available = new List<EmulatorOptionInfo>();

        // ReFix Online (Valve mode)
        var valveKey = $"{instance.AppId}_refix_valve";
        var valveStats = _ratings.TryGetValue(valveKey, out var vs) ? vs : new RatingData { Positive = 0, Negative = 0 };
        var isValveActive = string.Equals(instance.EmulatorId, "refix_valve", StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(instance.EmulatorId, "refix", StringComparison.OrdinalIgnoreCase) && instance.EmulatorEnabled);

        available.Add(new EmulatorOptionInfo
        {
            Id = "refix_valve",
            EmulatorId = "refix",
            Name = "ReFix Online (Steam Spacewar)",
            Mode = "valve",
            Description = "Uses Steam client with Spacewar (AppID 480) for matchmaking, invites, and online multiplayer gaming.",
            BadgeText = "Steam Online",
            PositiveVotes = valveStats.Positive,
            NegativeVotes = valveStats.Negative,
            IsActive = isValveActive
        });

        // Re:Goldberg LAN / Offline mode (Available for Unreal, Godot, Unity, Generic)
        var goldbergKey = $"{instance.AppId}_refix_goldberg";
        var goldbergStats = _ratings.TryGetValue(goldbergKey, out var gs) ? gs : new RatingData { Positive = 0, Negative = 0 };
        var isGoldbergActive = string.Equals(instance.EmulatorId, "refix_goldberg", StringComparison.OrdinalIgnoreCase);

        available.Add(new EmulatorOptionInfo
        {
            Id = "refix_goldberg",
            EmulatorId = "refix",
            Name = "Re:Goldberg LAN (Steam-Free)",
            Mode = "goldberg",
            Description = "100% standalone local emulation based on Goldberg backend. Enables LAN play and local saves without requiring Steam.",
            BadgeText = "LAN Offline",
            PositiveVotes = goldbergStats.Positive,
            NegativeVotes = goldbergStats.Negative,
            IsActive = isGoldbergActive
        });

        // Rank by ScorePercentage (descending), then PositiveVotes (descending), then TotalVotes (descending)
        var sorted = available
            .OrderByDescending(o => o.ScorePercentage)
            .ThenByDescending(o => o.PositiveVotes)
            .ThenByDescending(o => o.TotalVotes)
            .ToList();

        // Mark the top one as recommended ONLY if it reaches the minimum threshold of 10 total votes
        if (sorted.Count > 0 && sorted[0].TotalVotes >= 10)
        {
            sorted[0] = sorted[0] with { IsRecommended = true };
        }

        return sorted.AsReadOnly();
    }

    private async Task FetchRemoteRatingsAsync(uint appId, CancellationToken ct)
    {
        if (appId == 0) return;

        try
        {
            var url = $"{_workerBaseUrl.TrimEnd('/')}/ratings?appId={appId}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));

            using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var dict = JsonSerializer.Deserialize<Dictionary<string, RatingData>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        var key = $"{appId}_{kvp.Key}";
                        // If this emulator option was reset, don't re-apply old remote votes
                        var isReset = _resetEmulators.Keys.Any(emu => key.Contains(emu, StringComparison.OrdinalIgnoreCase));
                        if (isReset) continue;

                        _ratings.AddOrUpdate(key, kvp.Value, (_, existing) => new RatingData
                        {
                            Positive = Math.Max(existing.Positive, kvp.Value.Positive),
                            Negative = Math.Max(existing.Negative, kvp.Value.Negative)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cloudflare ratings sync skipped or timed out: {Message}", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SubmitVoteAsync(uint appId, string optionId, bool isPositive, CancellationToken ct = default)
    {
        var key = $"{appId}_{optionId}";
        var stats = _ratings.AddOrUpdate(
            key,
            _ => new RatingData { Positive = isPositive ? 1 : 0, Negative = isPositive ? 0 : 1 },
            (_, existing) => new RatingData
            {
                Positive = isPositive ? existing.Positive + 1 : existing.Positive,
                Negative = isPositive ? existing.Negative : existing.Negative + 1
            });

        await SaveDataAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Voted locally for {Key}: Positive={Pos}, Negative={Neg}", key, stats.Positive, stats.Negative);

        // Asynchronously post to Cloudflare Worker
        _ = Task.Run(async () =>
        {
            try
            {
                var payload = new
                {
                    appId = appId,
                    optionId = optionId,
                    isPositive = isPositive
                };
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var url = $"{_workerBaseUrl.TrimEnd('/')}/vote";
                using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully synced vote to Cloudflare Worker for AppID {AppId} ({OptionId})", appId, optionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to sync vote to Cloudflare Worker: {Message}", ex.Message);
            }
        });

        return true;
    }

    /// <inheritdoc />
    public bool HasUserVoted(Guid instanceId, string optionId)
    {
        var voteKey = $"{instanceId}_{optionId}";
        return _userVotes.ContainsKey(voteKey);
    }

    /// <inheritdoc />
    public async Task RecordUserVoteFlagAsync(Guid instanceId, string optionId, CancellationToken ct = default)
    {
        var voteKey = $"{instanceId}_{optionId}";
        _userVotes[voteKey] = true;
        await SaveDataAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResetRatingsForEmulatorAsync(string emulatorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorId)) return;

        _logger.LogInformation("Resetting community votes and ratings to 0 for emulator {EmulatorId}...", emulatorId);
        _resetEmulators[emulatorId] = true;

        // Reset ratings keys related to this emulator
        var keysToReset = _ratings.Keys
            .Where(k => k.Contains($"_{emulatorId}", StringComparison.OrdinalIgnoreCase) ||
                        k.Contains(emulatorId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToReset)
        {
            _ratings[key] = new RatingData { Positive = 0, Negative = 0 };
        }

        // Clear user vote flags for this emulator
        var userVoteKeysToRemove = _userVotes.Keys
            .Where(k => k.Contains($"_{emulatorId}", StringComparison.OrdinalIgnoreCase) ||
                        k.Contains(emulatorId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var voteKey in userVoteKeysToRemove)
        {
            _userVotes.TryRemove(voteKey, out _);
        }

        await SaveDataAsync(ct).ConfigureAwait(false);
    }
}
