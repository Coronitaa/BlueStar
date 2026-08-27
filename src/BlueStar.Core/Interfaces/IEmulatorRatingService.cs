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
    public int TotalVotes => PositiveVotes + NegativeVotes;
    public bool HasEnoughVotesForScore => TotalVotes >= 10;
    public double ScorePercentage => TotalVotes > 0
        ? (double)PositiveVotes / TotalVotes * 100.0
        : 0.0;
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

    /// <summary>
    /// Resets all community votes and user voting flags to 0 for a given emulator (e.g. after an emulator version update).
    /// </summary>
    Task ResetRatingsForEmulatorAsync(string emulatorId, CancellationToken ct = default);
}
