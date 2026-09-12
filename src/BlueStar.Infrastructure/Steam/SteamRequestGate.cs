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
/// The single place every request to store.steampowered.com is paced.
/// </summary>
/// <remarks>
/// All of BlueStar's Steam traffic leaves from the person's own address — there is no account,
/// no token and no server in between — so the whole app gets one budget, not one per service.
/// Two services pacing themselves independently is what got addresses blocked: each stayed
/// inside its own limit while together they doubled the rate.
///
/// Interactive work jumps ahead of background work. A 429 or 403 stands the whole app down for
/// several minutes, growing with each repeat, because Steam's block outlasts a polite retry.
/// </remarks>
public static class SteamRequestGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private static DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private static int _pendingInteractive;
    private static int _consecutiveBlocks;

    /// <summary>Shortest gap between two requests, whoever makes them.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1600);

    /// <summary>First stand-down after a block. Doubles, up to <see cref="MaxCooldown"/>.</summary>
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(20);

    /// <summary>Whether Steam is currently refusing us.</summary>
    public static bool IsCoolingDown => DateTimeOffset.UtcNow < _cooldownUntil;

    /// <summary>When the current stand-down ends. Past time when there is none.</summary>
    public static DateTimeOffset CooldownUntil => _cooldownUntil;

    /// <summary>
    /// Raised the first time a stand-down begins, so callers can log it once instead of once per
    /// suppressed request.
    /// </summary>
    public static event Action<HttpStatusCode, DateTimeOffset>? Blocked;

    /// <summary>
    /// Waits for this caller's turn.
    /// </summary>
    /// <returns>
    /// A lease to dispose when the request finishes, or <c>null</c> when Steam is standing us
    /// down and the caller should give up rather than queue.
    /// </returns>
    public static async Task<IDisposable?> AcquireAsync(
        SteamRequestPriority priority, CancellationToken ct = default)
    {
        if (IsCoolingDown) return null;

        if (priority == SteamRequestPriority.Interactive)
        {
            Interlocked.Increment(ref _pendingInteractive);
        }

        try
        {
            // Background work stands aside while anything interactive is waiting.
            if (priority == SteamRequestPriority.Background)
            {
                while (Volatile.Read(ref _pendingInteractive) > 0)
                {
                    if (IsCoolingDown) return null;
                    await Task.Delay(120, ct).ConfigureAwait(false);
                }
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);

            if (IsCoolingDown)
            {
                Gate.Release();
                return null;
            }

            var wait = _nextAllowed - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch
                {
                    Gate.Release();
                    throw;
                }
            }

            _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
            return new Lease(priority);
        }
        catch
        {
            if (priority == SteamRequestPriority.Interactive)
            {
                Interlocked.Decrement(ref _pendingInteractive);
            }

            throw;
        }
    }

    /// <summary>
    /// Records that Steam refused a request, and stands the whole app down.
    /// </summary>
    public static void ReportBlocked(HttpStatusCode status)
    {
        var strikes = Interlocked.Increment(ref _consecutiveBlocks);

        var cooldown = TimeSpan.FromTicks(Math.Min(
            BaseCooldown.Ticks * (long)Math.Pow(2, Math.Min(strikes - 1, 3)),
            MaxCooldown.Ticks));

        var until = DateTimeOffset.UtcNow + cooldown;

        // Only the transition into a stand-down is worth announcing.
        var wasClear = _cooldownUntil <= DateTimeOffset.UtcNow;
        _cooldownUntil = until;

        if (wasClear)
        {
            Blocked?.Invoke(status, until);
        }
    }

    /// <summary>
    /// Records that Steam answered normally, clearing the escalation.
    /// </summary>
    public static void ReportSuccess() => Interlocked.Exchange(ref _consecutiveBlocks, 0);

    private sealed class Lease(SteamRequestPriority priority) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (priority == SteamRequestPriority.Interactive)
            {
                Interlocked.Decrement(ref _pendingInteractive);
            }

            Gate.Release();
        }
    }
}
