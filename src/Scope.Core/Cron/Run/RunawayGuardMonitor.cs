namespace Scope.Core.Cron.Run;

using Serilog;

/// <summary>
/// Why a run was stopped by the runaway guard. <see cref="None"/> means the run finished on its own
/// (the guard never tripped). The two trip reasons are recorded distinctly so the cron_runs error
/// message is auditable: token budget vs wall-clock.
/// </summary>
public enum RunawayTrip
{
    /// <summary>The run completed (or failed) on its own; the guard did not stop it.</summary>
    None,

    /// <summary>Cumulative wire tokens reached the hard force-kill threshold (budget + grace).</summary>
    TokenBudget,

    /// <summary>The run exceeded its wall-clock cutoff (catches a local loop that burns no API tokens).</summary>
    WallClock,
}

/// <summary>
/// Ambient runaway-guard monitor (T-2026-06-17-06). Runs alongside a harness launch and force-kills it
/// when either guard trips, INDEPENDENT of agent cooperation (the ambient-observability rule): it does
/// not ask the harness to stop, it cancels the launch (which terminates the child process tree).
/// <list type="bullet">
///   <item><b>Token budget</b>: polls the dispatch's cumulative wire tokens every
///   <see cref="CronRunawayGuard.MonitorInterval"/> and trips when they reach the hard threshold
///   (budget + grace). The proxy's terminal refusal fires earlier (at exactly the budget) so a
///   cooperative harness usually stops before this kill; the kill backstops a harness that ignores the
///   refusal or spins.</item>
///   <item><b>Wall-clock</b>: trips when elapsed time exceeds the effective cutoff. This is the only
///   guard that can stop a local infinite loop burning no API tokens (invisible to the proxy/meter).</item>
/// </list>
/// The monitor owns no process handle; it cancels a <see cref="CancellationTokenSource"/> the launch is
/// bound to. The launcher's process runner force-kills the tree on cancel (see SystemProcessRunner).
/// </summary>
public sealed class RunawayGuardMonitor(CronCostLookup costLookup)
{
    /// <summary>
    /// Watches the run until <paramref name="launchTask"/> completes or a guard trips. On a trip it
    /// cancels <paramref name="killSource"/> (terminating the harness) and returns the trip reason; if
    /// the launch finishes first it returns <see cref="RunawayTrip.None"/>. Never throws: a monitor
    /// failure must never crash the run (it just stops guarding). Returns immediately with
    /// <see cref="RunawayTrip.None"/> when neither guard is armed (no budget and no positive runtime).
    /// </summary>
    /// <param name="dispatchId">Dispatch whose cumulative tokens are polled for the budget guard.</param>
    /// <param name="hardKillThreshold">Cumulative-token kill threshold (budget + grace), or null to disarm the token guard.</param>
    /// <param name="maxRuntime">Wall-clock cutoff, or null/zero to disarm the wall-clock guard.</param>
    /// <param name="launchTask">The in-flight harness launch; the monitor stops when it completes.</param>
    /// <param name="killSource">Cancelled to force-kill the harness when a guard trips.</param>
    public async Task<RunawayTrip> WatchAsync(
        string dispatchId,
        long? hardKillThreshold,
        TimeSpan? maxRuntime,
        Task launchTask,
        CancellationTokenSource killSource)
    {
        var hasTokenGuard = hardKillThreshold is > 0;
        var hasWallClockGuard = maxRuntime is { } mr && mr > TimeSpan.Zero;
        if (!hasTokenGuard && !hasWallClockGuard)
            return RunawayTrip.None;

        var deadline = hasWallClockGuard ? DateTime.UtcNow + maxRuntime!.Value : (DateTime?)null;

        try
        {
            while (true)
            {
                // Stop as soon as the launch finishes on its own (poll-or-launch race resolved here).
                var finished = await Task.WhenAny(
                    launchTask,
                    Task.Delay(CronRunawayGuard.MonitorInterval, killSource.Token)).ConfigureAwait(false);
                if (finished == launchTask)
                    return RunawayTrip.None;

                if (killSource.IsCancellationRequested)
                    return RunawayTrip.None;

                // Wall-clock guard first (cheap, and a stalled local loop emits no tokens to poll).
                if (deadline is { } dl && DateTime.UtcNow >= dl)
                {
                    Log.Warning(
                        "Runaway guard: run exceeded its wall-clock cutoff ({Cutoff}); force-killing the harness.",
                        maxRuntime);
                    SafeCancel(killSource);
                    return RunawayTrip.WallClock;
                }

                // Token-budget guard: read the live cumulative wire tokens for the dispatch.
                if (hasTokenGuard)
                {
                    var tokens = costLookup.ResolveDispatchTokens(dispatchId);
                    if (tokens >= hardKillThreshold!.Value)
                    {
                        Log.Warning(
                            "Runaway guard: dispatch reached {Tokens} cumulative wire tokens (kill threshold {Threshold}); force-killing the harness.",
                            tokens, hardKillThreshold.Value);
                        SafeCancel(killSource);
                        return RunawayTrip.TokenBudget;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Either the launch's own cancellation propagated into our Task.Delay, or the kill we
            // requested. Distinguish: if WE cancelled, the trip reason was already returned above; any
            // other cancellation means the run is ending for an unrelated reason, so no guard trip.
            return RunawayTrip.None;
        }
        catch (Exception ex)
        {
            Log.Warning("Runaway guard monitor failed (non-fatal); guard disarmed for this run: {ErrorMessage}", ex.Message);
            return RunawayTrip.None;
        }
    }

    private static void SafeCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { /* race with run teardown; the run is already ending */ }
    }
}
