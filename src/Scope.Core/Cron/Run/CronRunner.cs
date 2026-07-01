namespace Scope.Core.Cron.Run;

using Microsoft.Data.Sqlite;
using Scope.Core.App;
using Scope.Core.Cron.Launch;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;
using Serilog;
using System.Globalization;

/// <summary>
/// Composes the shipped pieces (dispatch correlation, child-env injection, the harness launcher
/// contract, and cron persistence) into a run-now orchestration for a single cron-agent definition.
///
/// Sequence (roadmap runner section):
///  1. Concurrency guard: if a run for this definition is already 'running', write a 'skipped' row
///     with a reason and return it without launching.
///  2. Create a dispatch (same observability path as ScopeApp.CreateDispatchAsync) and a session key.
///  3. Insert a 'running' cron_runs row carrying the dispatch id + session key.
///  4. Build a <see cref="HarnessLaunchRequest"/> (repo, prompt, artifact dir, model, default
///     sandbox, child-only SCOPE_DISPATCH_ID/SCOPE_SESSION_KEY, and proxy env from the existing
///     proxy state contract).
///  5. Select the launcher for the definition's harness via <see cref="CronLauncherRegistry"/>.
///  6. Ensure deterministic project-local hook config for the selected host.
///  7. Launch and await.
///  8. Complete the dispatch.
///  9. Update the run row to completed/failed with exit code, completion time, and failure reason.
/// 10. Fill total_cost_usd only from real metered telemetry for the dispatch when present.
///
/// Must-never-crash: steps 8-10 and all observability are wrapped so they never throw out of the
/// runner. A harness failure (non-zero exit) is recorded honestly as 'failed'. A harness whose
/// capability is unavailable records a 'failed' row carrying the capability detail, never a silent
/// no-op. No host-specific launch knowledge lives here; the runner consumes only IHarnessLauncher.
/// </summary>
public sealed class CronRunner(
    CronService service,
    CronLauncherRegistry registry,
    string scopeDirectory,
    CronCostLookup costLookup)
{
    /// <summary>Subdirectory under the repo's <c>.scope/</c> where run artifacts land.</summary>
    public const string ArtifactRootName = "cron-runs";

    /// <summary>Agent type string written into obs_agent_spans for Scope-owned cron root spans.</summary>
    public const string CronAgentType = "cron";

    /// <summary>Ambient runaway-guard monitor (token-budget + wall-clock force-kill); shares the cost lookup's telemetry db.</summary>
    private readonly RunawayGuardMonitor _guard = new(costLookup);

    /// <summary>
    /// Begins a cron run for the given definition: reaps stale rows, records the dispatch, and
    /// atomically inserts the initial run row (running or skipped). Returns the initial
    /// <see cref="CronRunRecord"/> and a <see cref="Task"/> that covers the harness launch and
    /// finalization. The returned Task is already started; callers must not dispose the runner
    /// before awaiting it (or tracking it for graceful shutdown).
    ///
    /// Skip path: if the TOCTOU unique index fires (another run is already in flight), the
    /// initial row is set to 'skipped' and the returned Task is <see cref="Task.CompletedTask"/>
    /// (no background work). This path is reached only when <c>block_concurrent_runs</c> is false;
    /// the guard-ON path is resolved in <see cref="ScopeApp.CronStartRunAsync"/> before calling here.
    ///
    /// Idempotency key: when <paramref name="idempotencyKey"/> is provided, it is used as the
    /// dispatch_id for the new run so a replay request (same key) can locate the in-flight row
    /// via dispatch_id lookup.
    ///
    /// This seam exists so the server can return 202 immediately and hand the continuation Task
    /// to a tracked background-task store. CLI and supervisor continue to use
    /// <see cref="RunAsync"/>, which internally calls this seam.
    /// </summary>
    public async Task<(CronRunRecord Initial, Task Completion)> BeginRunAsync(
        CronAgentDefinition definition, CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        // 0. Reap stale running rows.
        ReapStaleSafe(definition.Label);

        // 1. Concurrency guard via TOCTOU partial unique index.
        // When an idempotency key is provided, use it as the dispatch_id so replay requests
        // can identify the in-flight run by its dispatch_id.
        var dispatchId = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.GetGuid7String()
            : idempotencyKey.Trim();
        var sessionKey = $"cron-{Guid.GetGuid7String()}";
        RecordDispatchSafe(dispatchId, sessionKey, definition.Label);
        RecordSessionOriginSafe(sessionKey);
        RecordCronPromptSubmitSafe(sessionKey, definition.Prompt);

        CronResult<CronRunRecord> running;
        try
        {
            running = service.RecordRun(new CronRunCreateRequest(
                definition.Id,
                DispatchId: dispatchId,
                SessionKey: sessionKey,
                CronRunStatuses.Running));
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            CompleteDispatchSafe(dispatchId);
            var inflightId = service.FindRunningRun(definition.Id)?.Id ?? "unknown";
            var skipReason = $"A run for '{definition.Label}' is already in progress (run {inflightId}).";
            var skip = service.RecordRun(new CronRunCreateRequest(
                definition.Id,
                DispatchId: null,
                SessionKey: null,
                CronRunStatuses.Skipped,
                SkipReason: skipReason,
                CompletedAt: DateTime.UtcNow.ToString("o")));
            var skipRow = skip.Value ?? SyntheticSkipRow(definition.Id, skipReason);
            return (skipRow, Task.CompletedTask);
        }

        if (!running.Success)
        {
            CompleteDispatchSafe(dispatchId);
            var failedRow = SyntheticFailedRow(definition.Id, dispatchId, sessionKey, running.Error);
            return (failedRow, Task.CompletedTask);
        }

        var initialRow = running.Value!;
        var runId = initialRow.Id;

        // Build the launch request up-front so it can be captured by the continuation Task.
        var request = BuildLaunchRequest(definition, dispatchId, sessionKey);

        var launcher = registry.Resolve(definition.Harness);
        if (launcher is null)
        {
            var detail = $"No harness launcher is registered for '{definition.Harness}'.";
            FinalizeRun(runId, dispatchId, CronRunStatuses.Failed, exitCode: null, detail);
            var failedRow = ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey,
                CronRunStatuses.Failed, null, detail);
            return (failedRow, Task.CompletedTask);
        }

        var hookActivation = EnsureProjectLocalHooksSafe(definition.Harness, definition.RepoPath);
        if (hookActivation.ExitCode != (int)ScopeExitCode.Success)
        {
            var detail = hookActivation.Stderr ?? hookActivation.Stdout ?? "Project-local hook activation failed.";
            FinalizeRun(runId, dispatchId, CronRunStatuses.Failed, exitCode: null, detail);
            var failedRow = ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey,
                CronRunStatuses.Failed, null, detail);
            return (failedRow, Task.CompletedTask);
        }

        // Capture the db path now so the background task can open its own connection independently
        // of the caller's connection lifetime (the caller may dispose its connection after returning).
        var dbPath = Path.Combine(scopeDirectory, "scope.db");
        var capturedScopeDir = scopeDirectory;

        // Return the 'running' row; the caller owns tracking the continuation Task.
        var completionTask = Task.Run(async () =>
        {
            HarnessLaunchResult result;
            RunawayTrip trip;
            try
            {
                (result, trip) = await LaunchWithGuardAsync(launcher, request, definition, dispatchId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var detail = $"Harness launch threw: {ex.Message}";
                await FinalizeRunBackgroundAsync(dbPath, capturedScopeDir, runId, dispatchId,
                    CronRunStatuses.Failed, exitCode: null, detail).ConfigureAwait(false);
                return;
            }

            var (status, failureReason) = ClassifyOutcome(result, trip, definition, dispatchId);
            var bgCostResult = await FinalizeRunBackgroundAsync(dbPath, capturedScopeDir, runId, dispatchId,
                status, result.ExitCode, failureReason).ConfigureAwait(false);
            // Backfill the cron session's cc_session_id from the host session id the harness reported
            // (result.ThreadId), now that the run has completed. The session row was created mid-run with
            // cc NULL; this is the universal-cc capture close (T-2026-06-19-04). Never throws; no-ops on
            // a null/empty ThreadId.
            BackfillCronCcSessionSafe(capturedScopeDir, sessionKey, result.ThreadId);
            // A guard-killed run records a span too (it ran and spent partial tokens); its ExitCode may
            // be null (killed before exit), so the span is materialized whenever timestamps are present.
            RecordRunSpanSafe(capturedScopeDir, sessionKey, dispatchId, definition.Label, result, status,
                bgCostResult.PinnedBreakdown, request.Model);
        });

        return (initialRow, completionTask);
    }

    /// <summary>
    /// Runs the given definition now (blocking). Always returns a <see cref="CronRunOutcome"/> whose
    /// run row is persisted; it does not throw for harness, telemetry, or capability failures.
    /// Used by the CLI (<c>scope cron run</c>) and the supervisor, which run in-process and need
    /// the final terminal result before returning.
    /// </summary>
    public async Task<CronRunOutcome> RunAsync(CronAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        // 0. Reap stale running rows best-effort so a permanently-stuck definition can self-heal
        //    before the concurrency guard and TOCTOU index are evaluated.
        ReapStaleSafe(definition.Label);

        // 1. Concurrency guard (TOCTOU-closed by the partial unique index ux_cron_runs_one_running).
        //    Attempt to insert a 'running' row; if the unique index rejects it, treat it exactly
        //    like the prior inflight check: record a 'skipped' row and return without launching.
        var dispatchId = Guid.GetGuid7String();
        var sessionKey = $"cron-{Guid.GetGuid7String()}";
        RecordDispatchSafe(dispatchId, sessionKey, definition.Label);
        RecordSessionOriginSafe(sessionKey);
        RecordCronPromptSubmitSafe(sessionKey, definition.Prompt);

        // 2. Try to insert the running row; the partial unique index provides the atomicity.
        CronResult<CronRunRecord> running;
        try
        {
            running = service.RecordRun(new CronRunCreateRequest(
                definition.Id,
                DispatchId: dispatchId,
                SessionKey: sessionKey,
                CronRunStatuses.Running));
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 /* SQLITE_CONSTRAINT_UNIQUE */)
        {
            // The partial unique index ux_cron_runs_one_running fired: another run is already in the
            // 'running' state. We key on the EXTENDED code (UNIQUE), not the primary SQLITE_CONSTRAINT
            // (19) which also covers NOT NULL / CHECK / FK / PK; a genuine malformed-row insert must
            // surface as a failed run below, never be silently converted into a 'skipped' outcome.
            CompleteDispatchSafe(dispatchId);
            var inflightId = service.FindRunningRun(definition.Id)?.Id ?? "unknown";
            var skipReason = $"A run for '{definition.Label}' is already in progress (run {inflightId}).";
            var skip = service.RecordRun(new CronRunCreateRequest(
                definition.Id,
                DispatchId: null,
                SessionKey: null,
                CronRunStatuses.Skipped,
                SkipReason: skipReason,
                CompletedAt: DateTime.UtcNow.ToString("o")));
            var skipRow = skip.Value ?? SyntheticSkipRow(definition.Id, skipReason);
            return new CronRunOutcome(skipRow, Launched: false, CapabilityDetail: null);
        }

        if (!running.Success)
        {
            // Could not persist a running row for a non-constraint reason; report as failed.
            CompleteDispatchSafe(dispatchId);
            return new CronRunOutcome(
                SyntheticFailedRow(definition.Id, dispatchId, sessionKey, running.Error),
                Launched: false,
                CapabilityDetail: null);
        }

        var runId = running.Value!.Id;

        // 4. Build the launch request (carries the enforced token budget + effective wall-clock cutoff
        //    and a budget-scoped proxy URL so the proxy can refuse new calls at the budget).
        var request = BuildLaunchRequest(definition, dispatchId, sessionKey);

        // 5. Resolve the launcher for the harness.
        var launcher = registry.Resolve(definition.Harness);
        if (launcher is null)
        {
            var detail = $"No harness launcher is registered for '{definition.Harness}'.";
            FinalizeRun(runId, dispatchId, CronRunStatuses.Failed, exitCode: null, detail);
            return new CronRunOutcome(ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey,
                CronRunStatuses.Failed, null, detail), Launched: false, CapabilityDetail: detail);
        }

        var hookActivation = EnsureProjectLocalHooksSafe(definition.Harness, definition.RepoPath);
        if (hookActivation.ExitCode != (int)ScopeExitCode.Success)
        {
            var detail = hookActivation.Stderr ?? hookActivation.Stdout ?? "Project-local hook activation failed.";
            FinalizeRun(runId, dispatchId, CronRunStatuses.Failed, exitCode: null, detail);
            return new CronRunOutcome(ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey,
                CronRunStatuses.Failed, null, detail), Launched: false, CapabilityDetail: detail);
        }

        // 6. Launch under the runaway guard. The launcher never throws for an unmet capability; it
        //    returns a result whose Capability explains the gap. A guard force-kill surfaces as a
        //    synthesized result with a non-null trip reason (and timestamps), never as an exception.
        HarnessLaunchResult result;
        RunawayTrip trip;
        try
        {
            (result, trip) = await LaunchWithGuardAsync(launcher, request, definition, dispatchId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: an adapter that throws unexpectedly must still record a failed run honestly.
            var detail = $"Harness launch threw: {ex.Message}";
            FinalizeRun(runId, dispatchId, CronRunStatuses.Failed, exitCode: null, detail);
            return new CronRunOutcome(ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey,
                CronRunStatuses.Failed, null, detail), Launched: false, CapabilityDetail: null);
        }

        // A guard-stopped run DID launch (it spent partial tokens / wall-clock), even if it was killed
        // before producing an exit code.
        var launched = result.ExitCode is not null || trip != RunawayTrip.None;
        var capabilityDetail = result.Capability.IsAvailable ? null : result.Capability.Detail;
        var (status, failureReason) = ClassifyOutcome(result, trip, definition, dispatchId);

        // 7/8/9. Complete dispatch, fill cost from real metering only, update the run row.
        var syncCostResult = FinalizeRun(runId, dispatchId, status, result.ExitCode, failureReason);
        // Backfill the cron session's cc_session_id from the host session id the harness reported
        // (result.ThreadId), regardless of `launched`: a session row exists from the origin stamp and a
        // captured host id is worth recording even on a non-launch outcome. Universal-cc capture close
        // (T-2026-06-19-04). Never throws; no-ops on a null/empty ThreadId.
        BackfillCronCcSessionSafe(scopeDirectory, sessionKey, result.ThreadId);
        if (launched)
            RecordRunSpanSafe(scopeDirectory, sessionKey, dispatchId, definition.Label, result, status,
                syncCostResult.PinnedBreakdown, request.Model);

        var finalRow = ReadRunOrSynthetic(runId, definition.Id, dispatchId, sessionKey, status,
            result.ExitCode, failureReason);
        return new CronRunOutcome(finalRow, launched, capabilityDetail);
    }

    /// <summary>
    /// Completes the dispatch, resolves real metered cost and provenance, and transitions the run row.
    /// Wrapped so a telemetry or persistence failure never escapes the runner. Uses the caller-supplied
    /// <see cref="CronService"/> (and its connection) -- suitable for the synchronous/blocking path.
    /// Returns the resolved <see cref="CronCostResult"/> so the caller can thread the pinned breakdown
    /// into the materialized span.
    /// </summary>
    private CronCostResult FinalizeRun(string runId, string dispatchId, string status, int? exitCode, string? failureReason)
    {
        CompleteDispatchSafe(dispatchId);

        var costResult = CronCostResult.Unavailable();
        try
        {
            costResult = costLookup.ResolveDispatchCost(dispatchId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron cost resolution failed (non-fatal): {ErrorMessage}", ex.Message);
        }

        try
        {
            var update = service.CompleteRun(runId, status, exitCode, costResult.Cost, failureReason, costResult.Provenance);
            if (!update.Success)
                Log.Warning("Cron run completion update failed (non-fatal): {Error}", update.Error);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron run completion update threw (non-fatal): {ErrorMessage}", ex.Message);
        }

        return costResult;
    }

    /// <summary>
    /// Async variant of <see cref="FinalizeRun"/> that opens its own SQLite connection. Used by the
    /// background-task continuation so it is independent of the caller's connection lifetime.
    /// Named "Background" to distinguish it from the sync path and avoid VSTHRD103 triggering
    /// on callers that correctly use the sync version on a shared connection.
    /// Must-never-crash: all operations are wrapped; failures are logged only.
    /// Returns the resolved <see cref="CronCostResult"/> so the caller can thread the pinned
    /// breakdown into the materialized span.
    /// </summary>
    private static async Task<CronCostResult> FinalizeRunBackgroundAsync(
        string dbPath, string capturedScopeDir,
        string runId, string dispatchId,
        string status, int? exitCode, string? failureReason)
    {
        // Complete dispatch in observability writer (uses its own connection internally).
        try
        {
            using var writer = new ObservabilityWriter(capturedScopeDir);
            writer.CompleteDispatch(dispatchId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron background dispatch complete failed (non-fatal): {ErrorMessage}", ex.Message);
        }

        var costResult = CronCostResult.Unavailable();
        try
        {
            var lookup = new CronCostLookup(Path.Combine(capturedScopeDir, "telemetry.db"));
            costResult = lookup.ResolveDispatchCost(dispatchId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron background cost resolution failed (non-fatal): {ErrorMessage}", ex.Message);
        }

        try
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbPath, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore MA0004
            var backgroundService = new CronService(new CronRepository(conn));
            var update = backgroundService.CompleteRun(runId, status, exitCode, costResult.Cost, failureReason, costResult.Provenance);
            if (!update.Success)
                Log.Warning("Cron background run completion update failed (non-fatal): {Error}", update.Error);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron background run completion update threw (non-fatal): {ErrorMessage}", ex.Message);
        }

        return costResult;
    }

    private void ReapStaleSafe(string label)
    {
        try
        {
            var reaped = service.ReapStaleRuns(CronService.DefaultMaxRunningAge);
            if (reaped > 0)
                Log.Information("Cron reaper freed {Count} stale running row(s) before running '{Label}'.", reaped, label);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron reaper failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    private void RecordDispatchSafe(string dispatchId, string sessionKey, string label)
    {
        try
        {
            using var writer = new ObservabilityWriter(scopeDirectory);
            writer.RecordDispatch(dispatchId, sessionKey, label);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron dispatch create failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Stamps the cron session's honest origin (<see cref="SessionOrigin.ScopeLaunched"/>) at the point
    /// the run is created, BEFORE any harness launch or hook capture. Cron is definitionally
    /// Scope-launched: the runner mints SCOPE_SESSION_KEY=cron-{uuid7} for the child, so tier 1 always
    /// applies. We stamp the label directly rather than via SessionKeyResolver because the runner itself
    /// runs in the supervisor process where SCOPE_SESSION_KEY is unset (it is injected into the CHILD
    /// only), so resolving in-process would mis-classify as Unattributed. Origin is set-once via COALESCE,
    /// so this creation-time stamp wins and a run that produces no captured hook event still has
    /// origin='scope-launched' instead of null. Never throws; failures are logged.
    /// </summary>
    private void RecordSessionOriginSafe(string sessionKey)
    {
        try
        {
            using var writer = new ObservabilityWriter(scopeDirectory);
            writer.RecordAgentSession(sessionKey, CronAgentType, CronAgentType,
                parentSessionKey: null, origin: SessionOrigin.ScopeLaunched.ToLabel());
        }
        catch (Exception ex)
        {
            Log.Warning("Cron session origin stamp failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Backfills the cron session's <c>cc_session_id</c> from the host session id the harness reported
    /// (<paramref name="threadId"/> = <c>HarnessLaunchResult.ThreadId</c>, populated by BOTH
    /// <see cref="Scope.Core.Cron.Launch.ClaudeCodeHarnessLauncher"/> (from <c>summary.SessionId</c>) and
    /// <see cref="Scope.Core.Cron.Launch.CodexHarnessLauncher"/> (from <c>summary.ThreadId</c>)). This is
    /// the universal-cc capture close (T-2026-06-19-04): the cron session row was created mid-run with
    /// cc NULL (the origin stamp + prompt submit), and the host id is only known at completion.
    /// <para>
    /// SET-ONCE / FILL-IF-NULL: <see cref="ObservabilityDb.UpsertAgentSession"/> writes
    /// <c>cc_session_id = COALESCE(obs_agent_sessions.cc_session_id, $ccSessionId)</c> -- the EXISTING
    /// value is the first COALESCE arg, so a pre-existing NULL is filled with the new value and a
    /// pre-existing non-null cc is NEVER clobbered. The re-stamped agent type/subtype/origin are likewise
    /// COALESCE set-once, so this upsert only fills the cc bridge column on the cron row.
    /// </para>
    /// <para>
    /// No-ops when <paramref name="threadId"/> is null/empty (a harness path that yielded no host id leaves
    /// the cron row's cc NULL; it then buckets under its base key in by_conversation and still reconciles).
    /// Never throws; a telemetry write must never fail a cron run. Failures are logged via Serilog.
    /// </para>
    /// </summary>
    private static void BackfillCronCcSessionSafe(string capturedScopeDir, string sessionKey, string? threadId)
    {
        if (string.IsNullOrEmpty(threadId)) return;

        try
        {
            using var writer = new ObservabilityWriter(capturedScopeDir);
            writer.RecordAgentSession(sessionKey, CronAgentType, CronAgentType,
                parentSessionKey: null, origin: null, ccSessionId: threadId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron cc-session backfill failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Surfaces the cron definition's prompt as the cron session's INITIATING privileged
    /// <c>user_submit</c> event (subtype <see cref="UserSubmitSubtype.CronPrompt"/>, turn 0) at the
    /// point the run is created, before any harness launch. This guarantees the initiating prompt is
    /// the strictly-first human-intervention row for any cron session, independent of payload capture
    /// or hook availability. The content is Scope-owned (<c>definition.Prompt</c>); no parsing.
    /// Dedup is automatic in <see cref="ObservabilityWriter.RecordUserSubmit"/> (keyed on
    /// session_key + content_hash), so redundant/replay paths collapse to one row. Never throws;
    /// observability must never crash the run. Failures are logged via Serilog.
    /// </summary>
    private void RecordCronPromptSubmitSafe(string sessionKey, string prompt)
    {
        try
        {
            using var writer = new ObservabilityWriter(scopeDirectory);
            writer.RecordUserSubmit(new UserSubmitEvent(
                sessionKey,
                UserSubmitSubtype.CronPrompt,
                prompt,
                TurnIndex: 0));
        }
        catch (Exception ex)
        {
            Log.Warning("Cron prompt user_submit record failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Activates Scope-owned hook capture for cron-launched hosts using only deterministic,
    /// project-local config files. This is the trust boundary for noninteractive runs:
    /// Codex's hook-trust bypass flag is acceptable only because this step writes the vetted
    /// <c>.codex/hooks.json</c> commands immediately before launch; Claude Code gets the same
    /// project-local <c>.claude/settings.json</c> activation story without global config writes.
    /// </summary>
    private static ScopeCommandResult EnsureProjectLocalHooksSafe(string harness, string repoPath)
    {
        try
        {
            var result = harness switch
            {
                CronHarnesses.Codex => CodexHookConfigWriter.Write(
                    Path.Combine(repoPath, ".codex", "hooks.json")),
                CronHarnesses.ClaudeCode => HookConfigWriter.Write(
                    Path.Combine(repoPath, ".claude", "settings.json")),
                _ => ScopeCommandResult.Success(),
            };

            if (result.Stderr.IsInhabited)
                Log.Information("{Message}", result.Stderr);

            return result;
        }
        catch (Exception ex)
        {
            Log.Warning("Cron project-local hook activation failed: {ErrorMessage}", ex.Message);
            return new ScopeCommandResult(
                (int)ScopeExitCode.Unknown,
                Stderr: $"Project-local hook activation failed: {ex.Message}");
        }
    }

    private void CompleteDispatchSafe(string dispatchId)
    {
        try
        {
            using var writer = new ObservabilityWriter(scopeDirectory);
            writer.CompleteDispatch(dispatchId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron dispatch complete failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Materializes a Scope-owned ROOT span in obs_agent_spans for a completed cron run.
    /// The span's [started_at, completed_at] window covers the run's events, making it their
    /// temporal parent so the trace rollup can attribute wire/exact tokens to the session.
    /// When <paramref name="pinnedBreakdown"/> is non-null, its cost snapshot is stored on the span
    /// (schema v6) so TraceRollup can reconstruct the exact pinned cost without re-deriving from the
    /// live pricing table. Only materializes when the run actually launched (ExitCode is not null)
    /// and both timestamps are non-null/non-empty. Never throws; failures are logged as warnings.
    /// </summary>
    private static void RecordRunSpanSafe(
        string capturedScopeDir,
        string sessionKey,
        string dispatchId,
        string agentName,
        HarnessLaunchResult result,
        string status,
        CostBreakdown? pinnedBreakdown,
        string? model)
    {
        if (string.IsNullOrEmpty(result.StartedAt) || string.IsNullOrEmpty(result.CompletedAt))
            return;

        try
        {
            // Serialize the pinned breakdown components to JSON for storage.
            // Using ScopeJsonSerializerContext for AOT-safe source-gen serialization.
            string? pinnedBreakdownJson = null;
            if (pinnedBreakdown is not null)
            {
                pinnedBreakdownJson = System.Text.Json.JsonSerializer.Serialize(
                    pinnedBreakdown.Components,
                    ScopeJsonSerializerContext.Default.ListCostBreakdownComponent);
            }

            using var writer = new ObservabilityWriter(capturedScopeDir);
            writer.RecordAgentSpan(
                sessionKey: sessionKey,
                dispatchId: dispatchId,
                parentSpanId: null,
                agentType: CronAgentType,
                agentName: agentName,
                startedAt: result.StartedAt,
                completedAt: result.CompletedAt,
                inputSummary: null,
                outputSummary: null,
                status: string.Equals(status, CronRunStatuses.Completed, StringComparison.Ordinal) ? "completed" : "failed",
                agentRef: null,
                totalTokens: result.Usage?.TotalTokens,
                inputTokens: result.Usage?.InputTokens,
                outputTokens: result.Usage?.OutputTokens,
                // DB boundary (T-2026-06-20-20 Piece 2): pinned_cost_usd is a SQLite REAL; the breakdown
                // total is decimal, so convert to double for persistence.
                pinnedCostUsd: pinnedBreakdown is { } pb ? (double?)pb.TotalUsd : null,
                pinnedModelId: pinnedBreakdown?.ModelId,
                pinnedPricesAsOf: pinnedBreakdown?.PricesAsOf,
                pinnedBreakdownJson: pinnedBreakdownJson,
                // Tier-2 recorded model: the model Scope launched the cron run with. Persisted even
                // when wire metering succeeds (pinned cost wins at rollup); it is the fallback price
                // basis for an un-metered cron run. No parsing: definition.Model is Scope-owned.
                model: string.IsNullOrWhiteSpace(model) ? null : model);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron run span record failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Builds the <see cref="HarnessLaunchRequest"/> for a run: repo/prompt/artifact dir/model/sandbox
    /// and correlation, PLUS the runaway-guard fields. The token budget is declared on the request (the
    /// launcher surfaces it to the agent and the supervisor uses it for the hard kill); the effective
    /// wall-clock cutoff is resolved (per-agent override or global default); and the proxy base URL is
    /// budget-scoped so the proxy can refuse new calls at the budget. When no proxy is advertised, the
    /// budget scope is harmless (there is simply no proxy to gate) and the wall-clock guard still applies.
    /// <para>
    /// Provider-specific proxy wiring:
    /// <list type="bullet">
    ///   <item><b>Codex harness</b>: resolves the OpenAI proxy state from <c>proxy.openai.json</c> and
    ///   reads <see cref="Launch.CodexHarnessLauncher.ApiKeyEnvVar"/> (<c>CODEX_API_KEY</c>) from the
    ///   supervisor process environment. Both must be present for metered routing; missing either leaves
    ///   the run un-metered (direct un-metered path -- ChatGPT-subscription / no-key case). The api-key
    ///   gate mirrors <see cref="InteractiveMeterGate"/>'s subscription carve-out.</item>
    ///   <item><b>All other harnesses (Claude Code, etc.)</b>: resolves the Anthropic proxy base URL
    ///   from <c>proxy.json</c>; no explicit api key is set (it flows via the inherited parent
    ///   environment, as it did before).</item>
    /// </list>
    /// </para>
    /// </summary>
    private HarnessLaunchRequest BuildLaunchRequest(CronAgentDefinition definition, string dispatchId, string sessionKey)
    {
        var artifactDir = BuildArtifactDirectory(definition.RepoPath, definition.Label);
        var effectiveMaxRuntime = CronRunawayGuard.EffectiveMaxRuntimeSeconds(definition.MaxRuntimeSeconds);

        string? proxyBaseUrl;
        string? apiKey = null;

        if (string.Equals(definition.Harness, CronHarnesses.Codex, StringComparison.Ordinal))
        {
            // Codex: api-key-gated metering via the OpenAI proxy. Read CODEX_API_KEY from the
            // supervisor env (never persisted; scoped to the child process by BuildChildEnvironment).
            // If the key is absent (ChatGPT-subscription / no-key path) or no OpenAI proxy is
            // advertised, ProxyBaseUrl stays null and the run executes un-metered.
            var codexApiKey = Environment.GetEnvironmentVariable(Launch.CodexHarnessLauncher.ApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(codexApiKey))
            {
                var openAiBaseUrl = CronProxyEnvironment.TryResolveOpenAiBaseUrl(scopeDirectory);
                if (openAiBaseUrl is not null)
                {
                    apiKey = codexApiKey;
                    proxyBaseUrl = CronProxyEnvironment.WithBudgetScope(openAiBaseUrl, sessionKey, definition.TokenBudget);
                }
                else
                {
                    proxyBaseUrl = null; // no OpenAI proxy advertised -- un-metered
                }
            }
            else
            {
                proxyBaseUrl = null; // no api key -- ChatGPT-subscription path, un-metered
            }
        }
        else
        {
            // Claude Code and other harnesses: existing Anthropic proxy path (no explicit api key).
            var baseUrl = CronProxyEnvironment.TryResolveBaseUrl(scopeDirectory);
            proxyBaseUrl = CronProxyEnvironment.WithBudgetScope(baseUrl, sessionKey, definition.TokenBudget);
        }

        return new HarnessLaunchRequest
        {
            RepoPath = definition.RepoPath,
            Prompt = definition.Prompt,
            ArtifactDirectory = artifactDir,
            Model = definition.Model,
            Sandbox = HarnessSandbox.WorkspaceWrite,
            DispatchId = dispatchId,
            SessionKey = sessionKey,
            ProxyBaseUrl = proxyBaseUrl,
            ApiKey = apiKey,
            TokenBudget = definition.TokenBudget,
            MaxRuntimeSeconds = effectiveMaxRuntime,
        };
    }

    /// <summary>
    /// Launches the harness under the ambient runaway guard. Arms the token-budget guard (hard kill at
    /// budget + grace) and the wall-clock guard (per-agent or global cutoff), bound to a kill source the
    /// process runner terminates on cancel. Returns the launch result and the trip reason:
    /// <list type="bullet">
    ///   <item><see cref="RunawayTrip.None"/>: the run finished on its own; the real result is returned.</item>
    ///   <item>token-budget / wall-clock: the guard force-killed the harness. The launcher throws
    ///   <see cref="OperationCanceledException"/>; we synthesize a result with timestamps (so the partial
    ///   cost/tokens spent up to the cut are still attributed and a span is materialized) and a null exit
    ///   code, paired with the trip reason.</item>
    /// </list>
    /// The caller links the parent token: an external cancellation (caller asked to stop) also kills the
    /// child but yields <see cref="RunawayTrip.None"/>. A None trip alone does NOT mean "failed": the
    /// cooperative path (the metering proxy returns a terminal 403 at the budget and the harness exits
    /// non-zero BEFORE the guard force-kills) also yields None. <see cref="ClassifyOutcome"/> disambiguates
    /// these -- a non-zero exit whose cumulative wire tokens reached the budget is recorded as
    /// budget_exhausted, not failed; a genuine non-budget failure (or an external-cancel kill below budget)
    /// stays failed.
    /// </summary>
    private async Task<(HarnessLaunchResult Result, RunawayTrip Trip)> LaunchWithGuardAsync(
        IHarnessLauncher launcher,
        HarnessLaunchRequest request,
        CronAgentDefinition definition,
        string dispatchId,
        CancellationToken cancellationToken)
    {
        var hardKill = CronRunawayGuard.HardKillThreshold(definition.TokenBudget);
        var maxRuntime = request.MaxRuntimeSeconds is int s and > 0 ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        // No guard armed (no budget and no positive runtime): launch directly, preserving prior behavior.
        if (hardKill is null && maxRuntime is null)
        {
            var direct = await launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
            return (direct, RunawayTrip.None);
        }

        using var killSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startedAt = DateTime.UtcNow.ToString("o");
        var launchTask = launcher.LaunchAsync(request, killSource.Token);

        var trip = await _guard.WatchAsync(dispatchId, hardKill, maxRuntime, launchTask, killSource)
            .ConfigureAwait(false);

        try
        {
            var result = await launchTask.ConfigureAwait(false);
            // Launch completed without throwing. Either no guard tripped, or the kill raced a clean exit
            // that won; in both cases the harness produced a real result, so honor it (status None).
            return (result, RunawayTrip.None);
        }
        catch (OperationCanceledException) when (trip != RunawayTrip.None)
        {
            // The guard force-killed the harness. Synthesize a result carrying timestamps so the partial
            // cost/tokens spent up to the cut still attribute and a span is materialized; ExitCode stays
            // null (the process was killed, never produced a clean code).
            var reason = trip == RunawayTrip.TokenBudget
                ? BudgetExhaustedReason(definition.TokenBudget, dispatchId)
                : WallClockReason(request.MaxRuntimeSeconds);
            return (new HarnessLaunchResult
            {
                Capability = HarnessCapability.Available(),
                Succeeded = false,
                ExitCode = null,
                FailureReason = reason,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow.ToString("o"),
            }, trip);
        }
        catch (OperationCanceledException)
        {
            // External cancellation (the caller asked to stop), not a guard trip. Re-throw so the caller
            // records it as a failed launch rather than a budget exhaustion.
            throw;
        }
    }

    /// <summary>
    /// Maps a launch result + guard trip to the terminal cron_run status AND its recorded reason in one
    /// pass (a single cumulative-token read for the budget-cap check, so the status and the reason can
    /// never disagree). Three cases:
    /// <list type="bullet">
    ///   <item><b>Guard force-kill</b> (<paramref name="trip"/> != None): the supervisor's runaway guard
    ///   hard-killed the harness (token budget + grace, or wall-clock). Status is
    ///   <see cref="CronRunStatuses.BudgetExhausted"/> with the synthesized force-kill reason; unchanged.</item>
    ///   <item><b>Cooperative budget cutoff</b> (trip None, non-zero exit, a positive
    ///   <see cref="CronAgentDefinition.TokenBudget"/>, and the dispatch's cumulative wire tokens have
    ///   REACHED/EXCEEDED that budget): the metering proxy returned a terminal 403 (it refuses exactly when
    ///   cumulative &gt;= budget) and the harness cooperatively exited non-zero BEFORE the guard ever
    ///   force-killed (so trip stayed None). That is a working budget cutoff, not a crash, so it is recorded
    ///   as <see cref="CronRunStatuses.BudgetExhausted"/>, not <see cref="CronRunStatuses.Failed"/>.</item>
    ///   <item><b>Otherwise</b>: success exit -> completed; non-zero exit with cumulative &lt; budget (or no
    ///   budget) -> failed (a genuine non-budget failure), with the existing reason precedence.</item>
    /// </list>
    /// The cumulative-token read uses <see cref="CronCostLookup.ResolveDispatchTokens"/> -- the SAME source
    /// the runaway monitor and the cost lookup use -- so the comparison is exact (it does not re-derive or
    /// double-count cost; it only reads token totals). It is performed ONLY in the non-zero-exit + budget-set
    /// branch, so a clean success exit and an unbudgeted run pay no extra read.
    /// </summary>
    private (string Status, string? FailureReason) ClassifyOutcome(
        HarnessLaunchResult result, RunawayTrip trip, CronAgentDefinition definition, string dispatchId)
    {
        // Guard force-kill paths keep their existing budget_exhausted classification + synthesized reason.
        if (trip != RunawayTrip.None)
            return (CronRunStatuses.BudgetExhausted, result.FailureReason);

        // Clean success exit: completed, regardless of cumulative tokens (a run that finished its work
        // exactly at the budget is a completed run, not a budget exhaustion).
        if (result.Succeeded)
            return (CronRunStatuses.Completed, null);

        // Non-zero exit. Disambiguate a cooperative proxy-403 budget cutoff from a genuine failure: the
        // proxy refuses exactly when cumulative >= budget, so if a budget is set and the dispatch's
        // cumulative wire tokens have reached it, at least one call was refused and the harness stopped on
        // that refusal. Classify as budget_exhausted, not failed.
        if (definition.TokenBudget is int budget && budget > 0)
        {
            var cumulativeTokens = costLookup.ResolveDispatchTokens(dispatchId);
            if (cumulativeTokens >= budget)
                return (CronRunStatuses.BudgetExhausted,
                    CooperativeBudgetReason(budget, cumulativeTokens));
        }

        // Genuine non-budget failure: keep the existing reason precedence.
        var reason = result.FailureReason ?? (result.Capability.IsAvailable ? null : result.Capability.Detail);
        return (CronRunStatuses.Failed, reason);
    }

    private static string BudgetExhaustedReason(int? tokenBudget, string dispatchId)
        => $"Stopped by Scope: token budget reached (budget {tokenBudget?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}). " +
           "The harness was force-killed after exceeding budget + grace; partial cost recorded.";

    /// <summary>
    /// Reason recorded when a budgeted run hit the metering proxy's terminal 403 and the harness exited
    /// non-zero on its own (no guard force-kill). This is a working budget cutoff, surfaced honestly so a
    /// governance console does not show a crash where a budget cap actually fired.
    /// </summary>
    private static string CooperativeBudgetReason(int budget, long cumulativeTokens)
        => $"Token budget reached (budget {budget}, {cumulativeTokens} cumulative wire tokens); " +
           "the harness stopped on the proxy refusal (terminal 403). Not a failure: the budget cutoff fired.";

    private static string WallClockReason(int? maxRuntimeSeconds)
        => $"Stopped by Scope: wall-clock cutoff reached ({maxRuntimeSeconds?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}s). " +
           "The harness was force-killed; partial cost recorded.";

    private static string BuildArtifactDirectory(string repoPath, string label)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        return Path.Combine(repoPath, ".scope", ArtifactRootName, SanitizeLabel(label), timestamp);
    }

    /// <summary>Replaces filesystem-hostile characters in a label so it is safe as a path segment.</summary>
    private static string SanitizeLabel(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = label.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "run" : sanitized;
    }

    /// <summary>
    /// Reads the persisted run row back; if it cannot be read for any reason, returns a synthetic
    /// row carrying the same terminal facts so the adapter always has a record to report.
    /// </summary>
    private CronRunRecord ReadRunOrSynthetic(
        string runId, string agentId, string dispatchId, string sessionKey,
        string status, int? exitCode, string? failureReason)
    {
        try
        {
            var runs = service.ListRuns(agentId, limit: 100);
            var match = runs.FirstOrDefault(r => string.Equals(r.Id, runId, StringComparison.Ordinal));
            if (match is not null)
                return match;
        }
        catch (Exception ex)
        {
            Log.Warning("Cron run read-back failed (non-fatal): {ErrorMessage}", ex.Message);
        }

        return new CronRunRecord(runId, agentId, dispatchId, sessionKey,
            DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"),
            status, exitCode, null, null, failureReason);
    }

    private static CronRunRecord SyntheticSkipRow(string agentId, string reason)
        => new(Guid.GetGuid7String(), agentId, null, null,
            DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"),
            CronRunStatuses.Skipped, null, null, reason, null);

    private static CronRunRecord SyntheticFailedRow(string agentId, string dispatchId, string sessionKey, string? error)
        => new(Guid.GetGuid7String(), agentId, dispatchId, sessionKey,
            DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"),
            CronRunStatuses.Failed, null, null, null, error);
}
