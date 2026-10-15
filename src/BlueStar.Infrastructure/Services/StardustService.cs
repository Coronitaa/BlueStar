using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IStardustService"/>, querying and maintaining the Stardust balance
/// directly against the server ledger endpoint, distinguishing confirmed from pending points.
/// </summary>
public sealed class StardustService : IStardustService
{
    private readonly IVoluntaryContributionService _voluntaryContributionService;
    private readonly IContributionTransportService _contributionTransportService;
    private readonly ILogger<StardustService> _logger;
    private StardustBalanceInfo _currentBalance = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StardustBalanceInfo CurrentBalance => _currentBalance;

    public event EventHandler<StardustBalanceInfo>? BalanceChanged;

    public StardustService(
        IVoluntaryContributionService voluntaryContributionService,
        IContributionTransportService contributionTransportService,
        ILogger<StardustService>? logger = null)
    {
        _voluntaryContributionService = voluntaryContributionService ?? throw new ArgumentNullException(nameof(voluntaryContributionService));
        _contributionTransportService = contributionTransportService ?? throw new ArgumentNullException(nameof(contributionTransportService));
        _logger = logger ?? NullLogger<StardustService>.Instance;
    }

    /// <inheritdoc />
    public async Task<StardustBalanceInfo> RefreshBalanceAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var contributorId = await _voluntaryContributionService.GetOrCreateAnonymousContributorIdAsync(ct).ConfigureAwait(false);
            var balance = await _contributionTransportService.GetStardustBalanceAsync(contributorId, ct).ConfigureAwait(false);
            _currentBalance = balance;
            BalanceChanged?.Invoke(this, balance);
            return balance;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Stardust balance from server ledger");
            return _currentBalance;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void NotifyRewardConfirmed(int pointsAwarded)
    {
        if (pointsAwarded <= 0) return;

        _currentBalance = _currentBalance with
        {
            ConfirmedPoints = _currentBalance.ConfirmedPoints + pointsAwarded,
            LastUpdated = DateTimeOffset.UtcNow
        };

        BalanceChanged?.Invoke(this, _currentBalance);
    }
}
