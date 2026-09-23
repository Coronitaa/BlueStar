using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// How much a caller's request matters when several are queued for Steam.
/// </summary>
public enum SteamRequestPriority
{
    /// <summary>Someone is waiting for this on screen: a search, a page, a detail panel.</summary>
    Interactive,

    /// <summary>Filling something in that the page can live without: counts, badges.</summary>
    Background
}

/// <summary>
/// Gateway for pacing requests to store.steampowered.com, delegating to the domain-isolated
/// <see cref="DomainRateGovernor"/> with circuit breaker and jittered backoff.
/// </summary>
public static class SteamRequestGate
{
    /// <summary>Whether Steam store is currently refusing requests / cooling down.</summary>
    public static bool IsCoolingDown => DomainRateGovernor.Store.State == CircuitState.Open;

    /// <summary>When the current stand-down ends. Past time when there is none.</summary>
    public static DateTimeOffset CooldownUntil => DomainRateGovernor.Store.CooldownUntil;

    /// <summary>
    /// Raised the first time a stand-down begins, so callers can log it once instead of once per
    /// suppressed request.
    /// </summary>
    public static event Action<HttpStatusCode, DateTimeOffset>? Blocked;

    /// <summary>
    /// Waits for this caller's turn to access store.steampowered.com.
    /// </summary>
    public static async Task<IDisposable?> AcquireAsync(
        SteamRequestPriority priority, CancellationToken ct = default)
    {
        return await DomainRateGovernor.Store.AcquireAsync(priority, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that Steam refused a request, and trips the circuit breaker for store.steampowered.com.
    /// </summary>
    public static void ReportBlocked(HttpStatusCode status)
    {
        var wasClear = !IsCoolingDown;
        DomainRateGovernor.Store.ReportFailure(status);
        if (wasClear)
        {
            Blocked?.Invoke(status, CooldownUntil);
        }
    }

    /// <summary>
    /// Records that Steam answered normally, clearing the escalation.
    /// </summary>
    public static void ReportSuccess() => DomainRateGovernor.Store.ReportSuccess();
}
