namespace Scope.Cli.Cron;

using System.Collections.Concurrent;
using Scope.Core.Cron;
using Scope.Core.Cron.Run;
using Scope.Core.Entitlement;
using Scope.Core.Utilities;
using Serilog;

/// <summary>
/// Opt-in foreground cron supervisor (<c>scope cron start</c>).
///
/// Design choices (T-2026-06-12-29):
///   - Single-workspace MVP: operates on the current workspace's .scope/ database (the same
///     database discovered by all other cron CLI commands). No machine-level multi-repo registry
///     exists yet; when one is added it lives behind a separate command.
///   - No heartbeat/state file: T-27's age-based reaper (CronService.ReapStaleRuns /
///     DefaultMaxRunningAge ~6h) is the safety net for zombie runs left when the supervisor is
///     killed without a clean shutdown. A future task may add a supervisor.json state file for
///     richer status reporting.
///   - Bounded restart shape mirrors ProxyLauncher: the loop tracks per-definition consecutive
///     failures. After <see cref="MaxConsecutiveFailures"/> consecutive unexpected exceptions for
///     one definition, it is backed off for <see cref="BackOffMinutes"/> minutes so a broken
///     definition cannot spin-loop. Normal harness failures (non-zero exit) are already handled
///     by CronRunner as 'failed' run rows and do not count toward this back-off.
///   - Cancellation: Ctrl+C fires the CancellationToken; the sleep-to-next-minute path respects
///     it; all in-flight RunAsync tasks are awaited before exit.
///
/// Loop algorithm:
///   1. On start, resolve the .scope database from CWD (same as other cron commands).
///   2. Load all ENABLED definitions and print their next fire time AFTER now (no backfill).
///   3. Tick at minute granularity: sleep until the start of the next UTC minute, then check
///      each ENABLED definition against the current UTC minute.
///   4. When a definition is due, launch CronRunner.RunAsync concurrently. T-27's partial
///      unique index + skip path handles overlapping runs atomically; no concurrency logic here.
///   5. Per-definition errors are isolated: one failure never crashes the loop.
///   6. On Ctrl+C, wait for all in-flight tasks before exiting.
/// </summary>
public static class CronSupervisor
{
    private const int MaxConsecutiveFailures = 5;
    private const int BackOffMinutes = 30;

    /// <summary>
    /// Runs the supervisor loop until <paramref name="cancellationToken"/> is signalled.
    /// Returns 0 on clean cancellation.
    ///
    /// <paramref name="launcherRegistry"/> is optional; when null the production registry
    /// (<see cref="CronLauncherRegistry.CreateDefault"/>) is used. Pass an explicit registry
    /// in tests to inject a fake launcher without spawning a real process.
    ///
    /// <paramref name="tickDelay"/> overrides the production "sleep until next UTC minute"
    /// delay with a fixed short interval. Pass only from tests to make the loop tick quickly
    /// without waiting for a real minute boundary.
    ///
    /// <paramref name="entitlementProvider"/> is used for the B1 launch-time cap check. When
    /// null, a <see cref="DefaultEntitlementProvider"/> is used (reads disk on every tick).
    /// Pass an explicit provider in tests to control the cap without touching disk files.
    ///
    /// <paramref name="refresher"/> is optional. When non-null, the supervisor calls
    /// <see cref="IEntitlementRefresher.TryRefreshAsync"/> at startup and each tick so that
    /// online token renewal happens opportunistically. The throttle gate inside the refresher
    /// caps actual network calls to at most once per interval (default 4 hours).
    /// </summary>
    public static async Task<int> RunAsync(
        string dbPath, string scopeDir, CancellationToken cancellationToken,
        CronLauncherRegistry? launcherRegistry = null,
        TimeSpan? tickDelay = null,
        IEntitlementProvider? entitlementProvider = null,
        IEntitlementRefresher? refresher = null)
    {
        // B1: use DefaultEntitlementProvider if none supplied (reads disk on every GetState call).
        IEntitlementProvider provider = entitlementProvider ?? new DefaultEntitlementProvider();

        var utcNow = DateTime.UtcNow;
        Log.Information("scope cron start: supervisor starting at {UtcNow:u}.", utcNow);

        // Opportunistic refresh at supervisor startup. The throttle gate inside TryRefreshAsync
        // makes this a no-op if a refresh happened recently. Never awaited longer than the
        // cancellation token allows; failures are swallowed (refresher must-never-throw).
        if (refresher is not null)
        {
            _ = await refresher.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // Per-definition state for back-off tracking. ConcurrentDictionary because these are written
        // from the per-launch Task.Run workers (which may run several at once in one tick) while
        // backOffUntil is also read on the main loop thread.
        var consecutiveFailures = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var backOffUntil = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        // In-flight run tasks keyed by definition id. CronRunner persists all outcomes;
        // we only track Tasks to wait on them at shutdown and avoid redundant launches.
        var inFlight = new Dictionary<string, Task>(StringComparer.Ordinal);

        // Print startup banner with the initial schedule.
        await PrintStartupBannerAsync(dbPath, utcNow, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStart = DateTime.UtcNow;

            // Compute sleep duration until the next whole UTC minute, unless a test-only
            // tickDelay override is provided (avoids waiting up to 60 s in tests).
            int sleepMs;
            if (tickDelay.HasValue)
            {
                sleepMs = (int)tickDelay.Value.TotalMilliseconds;
            }
            else
            {
                var nextMinute = new DateTime(loopStart.Year, loopStart.Month, loopStart.Day,
                    loopStart.Hour, loopStart.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
                sleepMs = (int)(nextMinute - loopStart).TotalMilliseconds;
            }
            if (sleepMs > 0)
            {
                try
                {
                    await Task.Delay(sleepMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            var tickNow = DateTime.UtcNow;
            // Truncate to the minute for deterministic schedule matching.
            var tickMinute = new DateTime(tickNow.Year, tickNow.Month, tickNow.Day,
                tickNow.Hour, tickNow.Minute, 0, DateTimeKind.Utc);

            // Reload definitions each tick so create/enable/disable changes take effect.
            List<CronAgentDefinition> enabledDefs;
            try
            {
                enabledDefs = await LoadEnabledDefinitionsAsync(dbPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning("scope cron start: failed to load definitions (non-fatal, will retry next minute): {Error}", ex.Message);
                continue;
            }

            // Prune completed in-flight tasks.
            foreach (var key in inFlight.Keys.ToList())
            {
                if (inFlight[key].IsCompleted)
                    inFlight.Remove(key);
            }

            // Opportunistic per-tick refresh (S6 long-lived surface). The throttle gate inside
            // TryRefreshAsync caps actual network calls to at most once per interval (~4h).
            if (refresher is not null)
            {
                _ = await refresher.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            // B1 launch-time cap: read entitlement each tick so remote revocation takes effect
            // without a supervisor restart. GetState() reads disk on every call (no in-process
            // cache), so revocation is reflected within one tick interval.
            var entitlementState = provider.GetState();
            var maxEnabledCrons = entitlementState.EffectiveLimits.MaxEnabledCrons; // null = unlimited

            // B1 cap budget: how many NEW crons we may launch this tick.
            // In-flight tasks already count toward the cap regardless of which defs they are;
            // we never kill in-flight tasks — only new launches are gated.
            int newLaunchBudget = maxEnabledCrons is int cap
                ? Math.Max(0, cap - inFlight.Count)
                : int.MaxValue;
            int launchedThisTick = 0;

            // Stable deterministic ordering for cap enforcement: sort by (created_at, id) so the
            // same set of crons is always prioritised consistently when the cap is binding.
            // This ensures a revoked pilot with N>cap crons stops the LAST-created excess, not
            // a random selection that changes each tick.
            var sortedEnabledDefs = enabledDefs
                .OrderBy(d => d.CreatedAt, StringComparer.Ordinal)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var def in sortedEnabledDefs)
            {
                // Back-off check: skip definitions that are cooling down.
                if (backOffUntil.TryGetValue(def.Id, out var backOffEnd) && tickMinute < backOffEnd)
                    continue;

                // Compute next fire time from one minute before the current tick so the tick
                // minute itself is a candidate (NextFireAfter returns the FIRST minute strictly
                // after the supplied instant, so using tickMinute-1m yields tickMinute if it matches).
                var searchFrom = tickMinute.AddMinutes(-1);
                DateTime? nextFire;
                try
                {
                    nextFire = CronScheduleEvaluator.NextFireAfter(def.Schedule, searchFrom);
                }
                catch (Exception ex)
                {
                    Log.Warning("scope cron start: schedule evaluation failed for '{Label}' (skipping tick): {Error}",
                        def.Label, ex.Message);
                    continue;
                }

                // The definition fires only when the next fire time equals this tick's minute.
                if (nextFire is null || nextFire.Value != tickMinute)
                    continue;

                // Skip if this definition is already in-flight. T-27's TOCTOU guard would also
                // catch this atomically, but we avoid a redundant open connection + insert.
                if (inFlight.ContainsKey(def.Id))
                {
                    Log.Debug("scope cron start: '{Label}' is still in-flight; skipping this tick.", def.Label);
                    continue;
                }

                // B1 cap enforcement: check launch budget AFTER schedule and in-flight checks so
                // only defs that would actually fire this tick consume budget. Definitions excluded
                // by the cap are skipped (not killed); they remain enabled and eligible for future
                // ticks once revocation is cleared or additional capacity is granted.
                if (launchedThisTick >= newLaunchBudget)
                {
                    Log.Information(
                        "scope cron start: '{Label}' skipped — entitlement cap ({Cap} max, {InFlight} in-flight, {Launched} launched this tick).",
                        def.Label, maxEnabledCrons, inFlight.Count, launchedThisTick);
                    continue;
                }

                Log.Information("scope cron start: launching '{Label}' scheduled at {Tick:u}.", def.Label, tickMinute);
                launchedThisTick++;

                // Capture loop variables for the async task closure.
                var capturedDef = def;
                var capturedDbPath = dbPath;
                var capturedScopeDir = scopeDir;

                // Use CancellationToken.None for the launch task so it is not cancelled by Ctrl+C
                // mid-run (we wait for completion below instead).
                var task = Task.Run(async () =>
                {
                    try
                    {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                        await using var conn = await SQLite.OpenDbAsync(capturedDbPath, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore MA0004
                        var repo = new CronRepository(conn);
                        var service = new CronService(repo);
                        var costLookup = new CronCostLookup(
                            System.IO.Path.Combine(capturedScopeDir, "telemetry.db"));
                        var registry = launcherRegistry ?? CronLauncherRegistry.CreateDefault();
                        var runner = new CronRunner(service, registry,
                            capturedScopeDir, costLookup);

                        var outcome = await runner.RunAsync(capturedDef, CancellationToken.None).ConfigureAwait(false);
                        Log.Information(
                            "scope cron start: '{Label}' finished status={Status} launched={Launched}.",
                            capturedDef.Label, outcome.Run.Status, outcome.Launched);

                        // Reset back-off state on any completion (the CronRunner handles honest
                        // failed/skipped rows; unexpected throws tracked separately below).
                        consecutiveFailures.TryRemove(capturedDef.Id, out _);
                        backOffUntil.TryRemove(capturedDef.Id, out _);
                    }
                    catch (Exception ex)
                    {
                        // Atomic read-modify-write so concurrent workers cannot lose an increment.
                        var failures = consecutiveFailures.AddOrUpdate(capturedDef.Id, 1, (_, prev) => prev + 1);
                        Log.Warning(
                            "scope cron start: '{Label}' threw unexpectedly (failure {Count}/{Max}): {Error}",
                            capturedDef.Label, failures, MaxConsecutiveFailures, ex.Message);

                        if (failures >= MaxConsecutiveFailures)
                        {
                            var resumeAt = DateTime.UtcNow.AddMinutes(BackOffMinutes);
                            backOffUntil[capturedDef.Id] = resumeAt;
                            Log.Warning(
                                "scope cron start: '{Label}' hit {Max} consecutive failures; backing off until {ResumeAt:u}.",
                                capturedDef.Label, MaxConsecutiveFailures, resumeAt);
                        }
                    }
                });

                inFlight[def.Id] = task;
            }
        }

        // Drain in-flight tasks before exiting cleanly.
        if (inFlight.Count > 0)
        {
            Log.Information("scope cron start: draining {Count} in-flight run(s) before exit...", inFlight.Count);
            try
            {
                await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
            }
            catch
            {
                // Individual task errors are already handled; swallow aggregate here.
            }
        }

        Log.Information("scope cron start: supervisor stopped.");
        return 0;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task PrintStartupBannerAsync(string dbPath, DateTime utcNow, CancellationToken ct)
    {
        try
        {
            var defs = await LoadEnabledDefinitionsAsync(dbPath, ct).ConfigureAwait(false);
            if (defs.Count == 0)
            {
                await Console.Error.WriteLineAsync("scope cron start: no enabled cron agents found. " +
                    "Create one with 'scope cron create' or enable an existing one with 'scope cron enable <label>'.").ConfigureAwait(false);
                return;
            }

            await Console.Error.WriteLineAsync($"scope cron start: {defs.Count} enabled agent(s) scheduled (UTC):").ConfigureAwait(false);
            foreach (var def in defs)
            {
                string nextStr;
                try
                {
                    var next = CronScheduleEvaluator.NextFireAfter(def.Schedule, utcNow);
                    nextStr = next.HasValue ? next.Value.ToString("u") : "(never within 8-year lookahead)";
                }
                catch
                {
                    nextStr = "(schedule error)";
                }
                await Console.Error.WriteLineAsync($"  {def.Label} [{def.Schedule}] next: {nextStr}").ConfigureAwait(false);
            }

            await Console.Error.WriteLineAsync("scope cron start: running. Ctrl+C to stop.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning("scope cron start: startup banner failed (non-fatal): {Error}", ex.Message);
        }
    }

    private static async Task<List<CronAgentDefinition>> LoadEnabledDefinitionsAsync(
        string dbPath, CancellationToken ct)
    {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
        await using var conn = await SQLite.OpenDbAsync(dbPath, ct).ConfigureAwait(false);
#pragma warning restore MA0004
        var service = new CronService(new CronRepository(conn));
        return service.List().Where(d => d.Enabled).ToList();
    }
}
