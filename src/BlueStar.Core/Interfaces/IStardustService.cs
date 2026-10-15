using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service providing centralized, live access to the user's Stardust balance
/// fetched directly from the authorized Hub ledger endpoint.
/// </summary>
public interface IStardustService
{
    /// <summary>
    /// Gets the current verified Stardust balance.
    /// </summary>
    StardustBalanceInfo CurrentBalance { get; }

    /// <summary>
    /// Event raised whenever the Stardust balance is updated.
    /// </summary>
    event EventHandler<StardustBalanceInfo>? BalanceChanged;

    /// <summary>
    /// Fetches the latest verified balance from the Hub server ledger.
    /// </summary>
    Task<StardustBalanceInfo> RefreshBalanceAsync(CancellationToken ct = default);

    /// <summary>
    /// Records an accredited reward locally and updates subscribers.
    /// </summary>
    void NotifyRewardConfirmed(int pointsAwarded);
}
