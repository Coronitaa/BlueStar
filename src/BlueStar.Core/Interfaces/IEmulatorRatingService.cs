using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Model for an emulator option with community ratings and voting stats.
/// </summary>
public record EmulatorOptionInfo
{
    public required string Id { get; init; }
    public required string EmulatorId { get; init; }
    public required string Name { get; init; }
    public required string Mode { get; init; }
    public required string Description { get; init; }
    public required string BadgeText { get; init; }
    public int PositiveVotes { get; init; }
    public int NegativeVotes { get; init; }
    public double ScorePercentage => (PositiveVotes + NegativeVotes) > 0
        ? (double)PositiveVotes / (PositiveVotes + NegativeVotes) * 100.0
        : 100.0;
    public int TotalVotes => PositiveVotes + NegativeVotes;
    public bool IsRecommended { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>
/// Service that tracks, calculates, and synchronizes community votes for emulator options per game AppID.
/// </summary>
public interface IEmulatorRatingService
{
    /// <summary>
    /// Gets ranked emulator options with community voting stats for a given game instance.
    /// </summary>
    Task<IReadOnlyList<EmulatorOptionInfo>> GetOptionsForInstanceAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Submits a vote (positive or negative) for a specific emulator option on a game.
    /// </summary>
    Task<bool> SubmitVoteAsync(uint appId, string optionId, bool isPositive, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the user has already submitted a feedback vote for this instance and option.
    /// </summary>
    bool HasUserVoted(Guid instanceId, string optionId);

    /// <summary>
    /// Records that the user has voted or dismissed the feedback prompt for this instance and option.
    /// </summary>
    Task RecordUserVoteFlagAsync(Guid instanceId, string optionId, CancellationToken ct = default);
}
