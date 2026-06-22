namespace Scope.Core.App;

using System.Text.Json;
using System.Text.Json.Serialization;
using Scope.Core.Cron;
using Scope.Core.Cron.Launch;
using Scope.Core.Cron.Run;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;

public static partial class ScopeApp
{
    /// <summary>
    /// Lists all cron-agent definitions persisted in the workspace database, ordered by label.
    /// Resolves the database the same way as other read commands, then delegates to
    /// <see cref="CronService"/> for all business behavior.
    /// </summary>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the JSON definition list.</returns>
    public static async Task<ScopeCommandResult> CronListAgentsAsync(
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-list", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        List<CronAgentDto> dto;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var repo = new CronRepository(conn);
            var service = new CronService(repo);
            var agents = service.List();

            // Single query: latest run summary per agent (no N+1).
            var latestRuns = repo.GetLatestRunPerAgent()
                .ToDictionary(r => r.CronAgentId, r => r, StringComparer.Ordinal);

            dto = agents
                .Select(a => CronAgentDto.From(a, latestRuns.GetValueOrDefault(a.Id)))
                .ToList();
        }

        return ScopeCommandResult.Success(CronAgentDto.GetJsonString(dto));
    }

    /// <summary>
    /// Creates a cron-agent definition from a validated request. All validation, normalization,
    /// label-uniqueness, and free-tier limit enforcement remain in <see cref="CronService"/>;
    /// this method only resolves the database, delegates, and maps the result.
    /// </summary>
    /// <param name="request">Create request (already deserialized by the adapter).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>
    /// A <see cref="ScopeCommandResult"/> whose stdout is the created definition JSON on success,
    /// or a <see cref="ScopeExitCode.InvalidParameter"/> error when the service rejects the request.
    /// </returns>
    public static async Task<ScopeCommandResult> CronCreateAgentAsync(
        CronCreateRequest request,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-create", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<CronAgentDefinition> created;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            created = service.Create(request);
        }

        if (!created.Success)
            return Error(jsonErrors, ScopeExitCode.InvalidParameter, created.Error!);

        return ScopeCommandResult.Success(CronAgentDto.From(created.Value!).ToJsonString());
    }

    /// <summary>
    /// Updates (FULL REPLACE) the editable fields of an existing cron-agent definition by label
    /// (T-2026-06-17-05). All validation, normalization, and label-immutability remain in
    /// <see cref="CronService.Update"/> (sharing the create validator); this method only resolves the
    /// database, delegates, and maps the result. A missing or soft-deleted label maps to
    /// <see cref="ScopeExitCode.NoSymbolFound"/> (404); a validation rejection maps to
    /// <see cref="ScopeExitCode.InvalidParameter"/> (400).
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive); the immutable identity/URL key.</param>
    /// <param name="request">Update request (already deserialized by the adapter).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the updated definition JSON.</returns>
    public static async Task<ScopeCommandResult> CronUpdateAgentAsync(
        string label,
        CronUpdateRequest request,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-update", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<CronAgentDefinition> updated;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            updated = service.Update(label, request);
        }

        if (!updated.Success)
            return Error(jsonErrors, CronNotFoundCode(updated.Error), updated.Error!);

        return ScopeCommandResult.Success(CronAgentDto.From(updated.Value!).ToJsonString());
    }

    /// <summary>
    /// Partial (FETCH-MERGE) edit of an existing cron-agent definition by label, used by the
    /// NON-destructive CLI and MCP edit adapters (T-2026-06-17). Only the fields the caller
    /// explicitly set on <paramref name="patch"/> are overlaid on the agent's current values; an
    /// omitted field preserves the existing value. The merged full object is then validated and
    /// written through the SAME <see cref="CronService.Update"/> path as the full-replace server PUT,
    /// so validation cannot drift. The server endpoint keeps using
    /// <see cref="CronUpdateAgentAsync"/> (full replace), which the prefill-everything frontend relies
    /// on. A missing or soft-deleted label maps to <see cref="ScopeExitCode.NoSymbolFound"/> (404); a
    /// validation rejection of the merged state maps to <see cref="ScopeExitCode.InvalidParameter"/>
    /// (400).
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive); the immutable identity/URL key.</param>
    /// <param name="patch">Partial edit; null fields preserve the agent's current values.</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the updated definition JSON.</returns>
    public static async Task<ScopeCommandResult> CronEditAgentAsync(
        string label,
        CronUpdatePatch patch,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-update", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<CronAgentDefinition> updated;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            updated = service.UpdateMerged(label, patch);
        }

        if (!updated.Success)
            return Error(jsonErrors, CronNotFoundCode(updated.Error), updated.Error!);

        return ScopeCommandResult.Success(CronAgentDto.From(updated.Value!).ToJsonString());
    }

    /// <summary>
    /// Gets a single cron-agent definition by label. A missing label is mapped to
    /// <see cref="ScopeExitCode.NoSymbolFound"/> so adapters return 404; other service failures
    /// map to <see cref="ScopeExitCode.InvalidParameter"/> (400).
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the definition JSON on success.</returns>
    public static async Task<ScopeCommandResult> CronGetAgentAsync(
        string label,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-get", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<CronAgentDefinition> found;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            found = service.Get(label);
        }

        if (!found.Success)
            return Error(jsonErrors, CronNotFoundCode(found.Error), found.Error!);

        return ScopeCommandResult.Success(CronAgentDto.From(found.Value!).ToJsonString());
    }

    /// <summary>
    /// Removes a cron-agent definition by label (cascade-deletes its runs). A missing label maps
    /// to <see cref="ScopeExitCode.NoSymbolFound"/> (404); other failures map to 400.
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout reports the removed label.</returns>
    public static async Task<ScopeCommandResult> CronRemoveAgentAsync(
        string label,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-remove", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<bool> removed;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            removed = service.Remove(label);
        }

        if (!removed.Success)
            return Error(jsonErrors, CronNotFoundCode(removed.Error), removed.Error!);

        var response = new CronRemovedDto(label?.Trim() ?? "", true);
        return ScopeCommandResult.Success(response.ToJsonString());
    }

    /// <summary>
    /// Returns the run history for a cron agent identified by label. The label is resolved to an
    /// agent id first; a missing label maps to <see cref="ScopeExitCode.NoSymbolFound"/> (404).
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="limit">Maximum run rows to return (defaults to 50 when null).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the JSON run-record list.</returns>
    public static async Task<ScopeCommandResult> CronListRunsAsync(
        string label,
        int? limit,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-runs", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        ScopeCommandResult? failure = null;
        var runs = new List<CronRunDto>();
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            var agent = service.Get(label);
            if (!agent.Success)
            {
                failure = Error(jsonErrors, CronNotFoundCode(agent.Error), agent.Error!);
            }
            else
            {
                var records = service.ListRuns(agent.Value!.Id, limit ?? 50);
                runs = records.Select(CronRunDto.From).ToList();
            }
        }

        if (failure is not null) return failure;
        return ScopeCommandResult.Success(CronRunDto.GetJsonString(runs));
    }

    /// <summary>
    /// Lists cron runs across ALL agents (T-2026-06-15-16/ST-02), most-recent first, joined to each
    /// run's agent for the human label and schedule. Read-only over scope.db cron tables. Powers the
    /// /trace page Cron tab via GET /api/trace/cron-sessions; keyed by session_key so the frontend can
    /// deep-link into the trace/accounting detail for a cron session.
    /// </summary>
    /// <param name="limit">Maximum rows to return (clamped to [1, 500]).</param>
    /// <param name="from">Optional ISO-8601 UTC lower bound on started_at.</param>
    /// <param name="to">Optional ISO-8601 UTC upper bound on started_at.</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the JSON cron-session list.</returns>
    public static async Task<ScopeCommandResult> CronListSessionsAsync(
        int limit,
        string? from,
        string? to,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-sessions", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        var scopeDir = Path.GetDirectoryName(dbResult.Stdout!)!;
        var telemetryDbPath = Path.Combine(scopeDir, "telemetry.db");

        List<CronSessionDto> dto;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var repo = new CronRepository(conn);
            // Ensure the cron tables exist so a workspace that has never created a cron agent returns an
            // empty list (200 []) instead of failing on missing cron_runs/cron_agents tables. Idempotent.
            repo.EnsureSchema();
            var rows = repo.ListCronSessions(limit, from, to);

            // Cross-db: fetch deleted_at from telemetry.db for any cron session_keys.
            // Mirrors the CronCostLookup pattern: open telemetry.db separately, never ATTACH.
            // Rows with session_key=null (skipped runs, no obs session) are never "deleted".
            Dictionary<string, string?> deletedAtMap = [];
            var keysWithSession = rows
                .Where(r => r.SessionKey != null)
                .Select(r => r.SessionKey!)
                .ToList();

            if (keysWithSession.Count > 0 && File.Exists(telemetryDbPath))
            {
                try
                {
                    using var telConn = SQLite.OpenDb(telemetryDbPath);
                    var auditDb = new Telemetry.ObservabilityAuditDb(telConn);
                    deletedAtMap = auditDb.GetDeletedAtForSessions(keysWithSession);
                }
                catch (Exception ex)
                {
                    // Degrade gracefully: log and continue with empty map (all shown as non-deleted).
                    Serilog.Log.Warning("CronListSessions: telemetry.db deleted_at lookup failed (non-fatal): {ErrorMessage}", ex.Message);
                }
            }

            dto = rows
                .Select(r =>
                {
                    // Resolve deleted_at for this row's session_key (null key -> never deleted).
                    string? deletedAt = null;
                    if (r.SessionKey != null)
                        deletedAtMap.TryGetValue(r.SessionKey, out deletedAt);
                    return CronSessionDto.From(r, deletedAt);
                })
                // Filter: default excludes soft-deleted rows; includeDeleted surfaces them.
                .Where(d => includeDeleted || d.DeletedAt == null)
                .ToList();
        }

        return ScopeCommandResult.Success(CronSessionDto.GetJsonString(dto));
    }

    /// <summary>
    /// Runs a cron agent now: resolves the definition by label, then delegates to
    /// <see cref="CronRunner"/> which creates a dispatch, launches the harness via the launcher
    /// contract, and persists the cron_runs row. An unknown label maps to
    /// <see cref="ScopeExitCode.NoSymbolFound"/> (404). A run that did not launch because the harness
    /// capability is unavailable still returns success with a failed run body carrying the detail, so
    /// the frontend sees the honest degradation rather than a generic error.
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="registry">Optional launcher registry override (tests inject fakes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the run result JSON.</returns>
    public static async Task<ScopeCommandResult> CronRunAgentAsync(
        string label,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CronLauncherRegistry? registry = null,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-run", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        var resolvedRegistry = registry ?? CronLauncherRegistry.CreateDefault();
        var scopeDir = Path.GetDirectoryName(dbResult.Stdout!)!;

        ScopeCommandResult? failure = null;
        CronRunOutcome? outcome = null;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            var agent = service.Get(label);
            if (!agent.Success)
            {
                failure = Error(jsonErrors, CronNotFoundCode(agent.Error), agent.Error!);
            }
            else if (agent.Value!.DeletedAt is not null)
            {
                // Soft-deleted agent: GetAgentByLabel still resolves it (intentional, so historical
                // surfaces keep resolving the label), but a CLI run MUST NOT launch a deleted agent.
                // Map to the same not-found (404) shape as a label miss, never run it. This mirrors the
                // server run-now guard in CronStartRunAsync so both run entrypoints are symmetric.
                failure = Error(jsonErrors, ScopeExitCode.NoSymbolFound, $"Cron agent not found: {label.Trim()}");
            }
            else
            {
                var costLookup = new CronCostLookup(Path.Combine(scopeDir, "telemetry.db"));
                var runner = new CronRunner(service, resolvedRegistry, scopeDir, costLookup);
                outcome = await runner.RunAsync(agent.Value!, cancellationToken).ConfigureAwait(false);
            }
        }

        if (failure is not null) return failure;
        return ScopeCommandResult.Success(CronRunResultDto.From(outcome!).ToJsonString());
    }

    /// <summary>
    /// Starts a cron agent run and returns immediately (non-blocking). Resolves the definition by
    /// label, calls the begin/continue seam on <see cref="CronRunner"/>, and returns the initial
    /// run row (status running, skipped, or failed) together with the continuation <see cref="Task"/>
    /// that drives the harness launch and finalization on a server-owned background thread.
    ///
    /// The caller (server adapter) is responsible for tracking the returned <paramref name="completion"/>
    /// task so it is awaited on shutdown and failures are logged. This method never hands an untracked
    /// fire-and-forget Task to the runtime.
    ///
    /// Concurrency guard (R7-backend): when the agent's <c>block_concurrent_runs</c> flag is true and
    /// a run is already in flight:
    /// <list type="bullet">
    ///   <item>No idempotency key: returns <see cref="ScopeExitCode.ConcurrentRunConflict"/> (HTTP 409).
    ///     No new run row is created.</item>
    ///   <item>Matching idempotency key (key equals the running run's dispatch_id): returns the existing
    ///     in-flight run row (idempotent replay); no new row, no 409.</item>
    ///   <item>Non-matching key: still returns 409 (the key did not start the in-flight run).</item>
    /// </list>
    /// When <c>block_concurrent_runs</c> is false, the existing best-effort behaviour is preserved:
    /// the unique index still prevents a second 'running' row; the insert bounces to a 'skipped' row
    /// returned with 202.
    ///
    /// An unknown label maps to <see cref="ScopeExitCode.NoSymbolFound"/> (404), returned as a
    /// <c>null</c> completion task. The method always returns the initial <see cref="CronRunDto"/>
    /// on the success path so the server can emit 202 immediately.
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="registry">Optional launcher registry override (tests inject fakes).</param>
    /// <param name="idempotencyKey">Optional caller-supplied idempotency key (from the
    ///   <c>Idempotency-Key</c> request header). When provided and an in-flight run was started with
    ///   this key (its dispatch_id matches), the existing run is returned instead of 409.</param>
    /// <param name="cancellationToken">Cancellation token (used for the begin phase; the continuation uses CancellationToken.None).</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>
    /// On success: a <see cref="ScopeCommandResult"/> whose stdout is the initial run JSON, and a
    /// non-null <see cref="Task"/> for the background continuation (completed when the run was
    /// skipped or immediately failed). On 404/resolve failure: a failed result and null task.
    /// On 409 conflict (guard ON, no matching key): a <see cref="ScopeExitCode.ConcurrentRunConflict"/>
    /// result and null task; the stdout carries the in-flight run JSON.
    /// </returns>
    public static async Task<(ScopeCommandResult Result, Task? Completion)> CronStartRunAsync(
        string label,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CronLauncherRegistry? registry = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-start-run", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return (dbResult, null);

        var resolvedRegistry = registry ?? CronLauncherRegistry.CreateDefault();
        var scopeDir = Path.GetDirectoryName(dbResult.Stdout!)!;

        ScopeCommandResult? failure = null;
        CronRunRecord? initial = null;
        Task? completion = null;

        using (ctx.TimeStage("begin"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            var agent = service.Get(label);
            if (!agent.Success)
            {
                failure = Error(jsonErrors, CronNotFoundCode(agent.Error), agent.Error!);
            }
            else if (agent.Value!.DeletedAt is not null)
            {
                // Soft-deleted agent: GetAgentByLabel still resolves it (intentional, so historical
                // surfaces keep resolving the label), but a run-now MUST NOT launch a deleted agent.
                // Map to the same not-found (404) shape as a label miss, never run it.
                failure = Error(jsonErrors, ScopeExitCode.NoSymbolFound, $"Cron agent not found: {label.Trim()}");
            }
            else
            {
                var definition = agent.Value!;

                // Concurrency guard (R7-backend): when block_concurrent_runs is true, a second
                // run-now for the same agent must not start. Check BEFORE calling BeginRunAsync so
                // we can distinguish a deliberate 409 (guard ON) from the legacy skip path (guard OFF).
                if (definition.BlockConcurrentRuns)
                {
                    var inflight = service.FindRunningRun(definition.Id);
                    if (inflight is not null)
                    {
                        // Idempotent replay: the key matches the in-flight run's dispatch_id,
                        // meaning this request started that run. Return the existing run, not 409.
                        var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
                        if (normalizedKey is not null
                            && string.Equals(inflight.DispatchId, normalizedKey, StringComparison.Ordinal))
                        {
                            // Idempotent: return the existing in-flight run as 202.
                            var idempotentDto = CronRunDto.From(inflight);
                            return (ScopeCommandResult.Success(idempotentDto.ToJsonString()), Task.CompletedTask);
                        }

                        // Guard ON, no matching key: 409 Conflict. Carry the in-flight run details
                        // in stdout so the caller can surface them (run id, started_at, etc.).
                        var inFlightDto = CronRunDto.From(inflight);
                        var conflictResult = new ScopeCommandResult(
                            (int)ScopeExitCode.ConcurrentRunConflict,
                            Stdout: inFlightDto.ToJsonString(),
                            Stderr: Error(jsonErrors, ScopeExitCode.ConcurrentRunConflict,
                                $"A run for '{definition.Label}' is already in progress (run {inflight.Id}). " +
                                "Wait for it to complete; this agent blocks concurrent runs (block_concurrent_runs is set at creation).").Stderr);
                        return (conflictResult, null);
                    }
                }

                // Guard OFF (or no inflight run): proceed with the normal begin path.
                // If block_concurrent_runs is false and a run is already in flight, the TOCTOU
                // unique index in BeginRunAsync will produce a 'skipped' row (legacy behavior).
                var costLookup = new CronCostLookup(Path.Combine(scopeDir, "telemetry.db"));
                var runner = new CronRunner(service, resolvedRegistry, scopeDir, costLookup);

                // When an idempotency key is provided, use it as the dispatch_id so the second
                // request (with the same key) can locate this run via dispatch_id lookup.
                (initial, completion) = await runner.BeginRunAsync(
                    definition, cancellationToken,
                    idempotencyKey: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim())
                    .ConfigureAwait(false);
            }
        }

        if (failure is not null) return (failure, null);
        var dto = CronRunDto.From(initial!);
        return (ScopeCommandResult.Success(dto.ToJsonString()), completion);
    }

    /// <summary>
    /// Returns a single cron run record by its id (used by the poll endpoint). An unknown label
    /// maps to <see cref="ScopeExitCode.NoSymbolFound"/> (404); an unknown run id on a known label
    /// also maps to 404 so the poll endpoint returns a clean 404 rather than a 500.
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive) — used to scope the lookup.</param>
    /// <param name="runId">The run row id returned by the start endpoint.</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the single-run JSON on success.</returns>
    public static async Task<ScopeCommandResult> CronGetRunAsync(
        string label,
        string runId,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-get-run", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronRunRecord? run = null;
        ScopeCommandResult? failure = null;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));

            // Validate the label first (ensures 404 rather than a cross-agent run id lookup).
            var agent = service.Get(label);
            if (!agent.Success)
            {
                failure = Error(jsonErrors, CronNotFoundCode(agent.Error), agent.Error!);
            }
            else
            {
                run = service.GetRunById(runId);
                if (run is null || !(string.Equals(run.CronAgentId, agent.Value!.Id, StringComparison.Ordinal)))
                    failure = Error(jsonErrors, ScopeExitCode.NoSymbolFound,
                        $"Cron run not found: {runId}");
            }
        }

        if (failure is not null) return failure;
        return ScopeCommandResult.Success(CronRunDto.From(run!).ToJsonString());
    }

    /// <summary>
    /// Probes each supported harness launcher and reports its capability status and detail for
    /// frontend display. Each probe is isolated: a launcher that throws is surfaced as an
    /// unsupported entry rather than crashing the endpoint. The Claude Code seam stub's honest
    /// non-available status is reported as-is, never special-cased.
    /// </summary>
    /// <param name="registry">Optional launcher registry override (tests inject fakes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the JSON capability array.</returns>
    public static async Task<ScopeCommandResult> CronCapabilitiesAsync(
        CronLauncherRegistry? registry = null,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-capabilities", dispatchId: ObservabilityContext.ResolveDispatchId());

        var resolvedRegistry = registry ?? CronLauncherRegistry.CreateDefault();
        var entries = new List<CronCapabilityDto>();
        using (ctx.TimeStage("execute"))
        {
            foreach (var launcher in resolvedRegistry.All)
            {
                HarnessCapability capability;
                try
                {
                    capability = await launcher.ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    capability = HarnessCapability.UnsupportedCli($"Capability probe failed: {ex.Message}");
                }

                entries.Add(new CronCapabilityDto(launcher.Harness, capability.Status.ToString(), capability.Detail));
            }
        }

        return ScopeCommandResult.Success(CronCapabilityDto.GetJsonString(entries));
    }

    /// <summary>
    /// Reports the current cron-agent status for the CLI: the persisted definitions plus each
    /// supported harness's capability. This is a CLI-shaped composition of
    /// <see cref="CronListAgentsAsync"/> and <see cref="CronCapabilitiesAsync"/> so the
    /// <c>scope cron status</c> command can emit both in a single JSON object. It reports no
    /// supervisor/enable/disable state (that is CR-M2 scope).
    /// </summary>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="registry">Optional launcher registry override (tests inject fakes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the combined status JSON.</returns>
    public static async Task<ScopeCommandResult> CronStatusAsync(
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CronLauncherRegistry? registry = null,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, "cron-status", dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        var resolvedRegistry = registry ?? CronLauncherRegistry.CreateDefault();

        List<CronAgentDto> agents;
        var capabilities = new List<CronCapabilityDto>();
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            agents = service.List().Select(CronAgentDto.From).ToList();

            foreach (var launcher in resolvedRegistry.All)
            {
                HarnessCapability capability;
                try
                {
                    capability = await launcher.ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    capability = HarnessCapability.UnsupportedCli($"Capability probe failed: {ex.Message}");
                }

                capabilities.Add(new CronCapabilityDto(launcher.Harness, capability.Status.ToString(), capability.Detail));
            }
        }

        var status = new CronStatusDto(agents, capabilities);
        return ScopeCommandResult.Success(status.ToJsonString());
    }

    /// <summary>
    /// Enables or disables a cron-agent definition by label, enforcing the local free-tier cap on
    /// enable. Returns the updated <see cref="CronAgentDto"/> on success. A missing label maps to
    /// <see cref="ScopeExitCode.NoSymbolFound"/> (404); a cap violation maps to
    /// <see cref="ScopeExitCode.InvalidParameter"/> (400). The operation is idempotent.
    /// </summary>
    /// <param name="label">Cron-agent label (case-insensitive).</param>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <param name="dbPath">Explicit database path, or null to discover from CWD.</param>
    /// <param name="searchAllParents">When true, discovery walks to the filesystem root.</param>
    /// <param name="jsonErrors">When true, expected errors are formatted as JSON on stderr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="ctx">Optional observability context for stage timing.</param>
    /// <returns>A <see cref="ScopeCommandResult"/> whose stdout is the updated definition JSON.</returns>
    public static async Task<ScopeCommandResult> CronSetEnabledAsync(
        string label,
        bool enabled,
        string? dbPath,
        bool searchAllParents,
        bool jsonErrors,
        CancellationToken cancellationToken = default,
        ObservabilityContext? ctx = null)
    {
        var operation = enabled ? "cron-enable" : "cron-disable";
        ctx ??= new ObservabilityContext(CallerSurface.Unknown, operation, dispatchId: ObservabilityContext.ResolveDispatchId());

        var dbResult = ResolveDb(dbPath, searchAllParents, jsonErrors);
        if (dbResult.ExitCode != (int)ScopeExitCode.Success) return dbResult;

        CronResult<CronAgentDefinition> updated;
        using (ctx.TimeStage("execute"))
        {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
            await using var conn = await SQLite.OpenDbAsync(dbResult.Stdout!, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
            var service = new CronService(new CronRepository(conn));
            updated = service.SetEnabled(label, enabled);
        }

        if (!updated.Success)
            return Error(jsonErrors, CronNotFoundCode(updated.Error), updated.Error!);

        return ScopeCommandResult.Success(CronAgentDto.From(updated.Value!).ToJsonString());
    }

    private static ScopeExitCode CronNotFoundCode(string? error)
        => error is not null && error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? ScopeExitCode.NoSymbolFound
            : ScopeExitCode.InvalidParameter;
}

// Public contract preserved (T-2026-06-17 sweep, owner): the cron DTO records below are registered in
// ScopeJsonSerializerContext with pinned [JsonPropertyName] fields and serialized as the server cron
// API / CLI JSON shapes; their List<T> members and List<T> static GetJsonString helpers map to
// registered serialized types (ListCronAgentDto, etc.). Concrete collection types are the wire shape.
#pragma warning disable MA0016 // public cron API response-DTO contract; concrete collection types are the serialized shape

/// <summary>
/// JSON-serializable view of a <see cref="CronAgentDefinition"/> for the server cron API.
/// <para>
/// The four <c>last_run_*</c> fields are populated only on the list path (via a single
/// group-by query in <see cref="CronRepository.GetLatestRunPerAgent"/>). All other paths
/// (create, get-by-label, update) use <see cref="From(CronAgentDefinition)"/> and leave
/// them null. A null <c>last_run_cost</c> will NEVER carry a <c>joined_exact</c> or
/// <c>wire_exact</c> provenance value.
/// </para>
/// </summary>
public sealed record CronAgentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("repo_path")] string RepoPath,
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("token_budget")] int? TokenBudget,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("block_concurrent_runs")] bool BlockConcurrentRuns = true,
    [property: JsonPropertyName("max_runtime_seconds")] int? MaxRuntimeSeconds = null,
    [property: JsonPropertyName("last_run_status")] string? LastRunStatus = null,
    [property: JsonPropertyName("last_run_at")] string? LastRunAt = null,
    [property: JsonPropertyName("last_run_cost")] double? LastRunCost = null,
    [property: JsonPropertyName("last_run_cost_provenance")] string? LastRunCostProvenance = null)
{
    /// <summary>
    /// Builds a DTO from a definition only, leaving all last-run fields null.
    /// Used by create, get-by-label, update, and enable/disable paths.
    /// </summary>
    public static CronAgentDto From(CronAgentDefinition agent)
        => new(agent.Id, agent.Label, agent.Schedule, agent.Prompt, agent.RepoPath,
            agent.Harness, agent.Model, agent.TokenBudget, agent.Enabled,
            agent.CreatedAt, agent.UpdatedAt, agent.BlockConcurrentRuns, agent.MaxRuntimeSeconds);

    /// <summary>
    /// Builds a DTO from a definition plus a last-run summary row. A null summary (agent has
    /// never run) leaves all last-run fields null. A null cost never carries an exact provenance.
    /// </summary>
    public static CronAgentDto From(CronAgentDefinition agent, CronAgentLastRunSummary? lastRun)
    {
        if (lastRun is null || lastRun.StartedAt is null)
            return From(agent);

        // A null cost must never carry wire_exact or joined_exact provenance.
        string? safeProvenance = lastRun.TotalCostUsd is null
            ? SanitizeNullCostProvenance(lastRun.CostProvenance)
            : lastRun.CostProvenance;

        return new(agent.Id, agent.Label, agent.Schedule, agent.Prompt, agent.RepoPath,
            agent.Harness, agent.Model, agent.TokenBudget, agent.Enabled,
            agent.CreatedAt, agent.UpdatedAt, agent.BlockConcurrentRuns, agent.MaxRuntimeSeconds,
            LastRunStatus: lastRun.Status,
            LastRunAt: lastRun.StartedAt,
            LastRunCost: lastRun.TotalCostUsd,
            LastRunCostProvenance: safeProvenance);
    }

    /// <summary>
    /// When cost is null, provenance values that imply an exact amount (wire_exact, joined_exact)
    /// are demoted to 'unavailable'. All other values pass through unchanged.
    /// </summary>
    private static string? SanitizeNullCostProvenance(string? provenance) =>
        provenance is Cron.Run.CostProvenance.WireExact or Cron.Run.CostProvenance.JoinedExact
            ? Cron.Run.CostProvenance.Unavailable
            : provenance;

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CronAgentDto);

    public static string GetJsonString(List<CronAgentDto> entries)
        => JsonSerializer.Serialize(entries, ScopeJsonSerializerContext.Default.ListCronAgentDto);
}

/// <summary>
/// JSON-serializable view of a <see cref="CronRunRecord"/> for the server cron API.
/// <para>
/// <c>cost_provenance</c> is the 6-value cost provenance string (wire_exact | joined_exact |
/// provider_estimated | scope_estimated | unmetered | unavailable). Null in the DB (schema v3
/// column absent for historical rows) is surfaced as "unavailable" so a null cost is NEVER
/// serialized as "joined_exact".
/// </para>
/// </summary>
public sealed record CronRunDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cron_agent_id")] string CronAgentId,
    [property: JsonPropertyName("dispatch_id")] string? DispatchId,
    [property: JsonPropertyName("session_key")] string? SessionKey,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("completed_at")] string? CompletedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("total_cost_usd")] double? TotalCostUsd,
    [property: JsonPropertyName("skip_reason")] string? SkipReason,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance)
{
    public static CronRunDto From(CronRunRecord run)
        => new(run.Id, run.CronAgentId, run.DispatchId, run.SessionKey, run.StartedAt,
            run.CompletedAt, run.Status, run.ExitCode, run.TotalCostUsd,
            run.SkipReason, run.ErrorMessage,
            // Null in the DB (historical rows pre-schema-v3) maps to 'unavailable'; a non-null
            // provenance from the DB is used as-is. A null cost is never 'joined_exact'.
            run.CostProvenance ?? Cron.Run.CostProvenance.Unavailable);

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CronRunDto);

    public static string GetJsonString(List<CronRunDto> entries)
        => JsonSerializer.Serialize(entries, ScopeJsonSerializerContext.Default.ListCronRunDto);
}

/// <summary>
/// Cross-agent cron run row for the /trace page Cron tab (T-2026-06-15-16/ST-02), reported as JSON by
/// GET /api/trace/cron-sessions. Joins each cron_run to its cron_agent for the human label + schedule;
/// keyed by session_key so the frontend can deep-link into the trace/accounting detail for the cron
/// session. <c>cost_provenance</c> maps a null DB value to 'unavailable' so a null cost is never
/// serialized as a metered provenance.
/// </summary>
public sealed record CronSessionDto(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_key")] string? SessionKey,
    // Short BASE36 session handle (same encoding as TraceSession.display_id and the trace detail /
    // deep-link), so the Cron tab can show WHICH session a run traces, not just the job. Null when the
    // run produced no session (e.g. skipped). T-2026-06-15-16 follow-up.
    [property: JsonPropertyName("display_id")] string? DisplayId,
    [property: JsonPropertyName("agent_label")] string AgentLabel,
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("total_cost_usd")] double? TotalCostUsd,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("completed_at")] string? CompletedAt,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance,
    // ISO-8601 UTC soft-delete timestamp from obs_agent_sessions.deleted_at (telemetry.db).
    // Null when the session has not been soft-deleted. Added T-2026-06-16-02.
    [property: JsonPropertyName("deleted_at")] string? DeletedAt = null)
{
    /// <summary>
    /// Builds a DTO from a cron session row, annotating with the resolved deleted_at value
    /// (null when the obs session is absent or not soft-deleted).
    /// </summary>
    public static CronSessionDto From(CronSessionRow row, string? deletedAt = null)
        => new(row.RunId, row.SessionKey,
            row.SessionKey != null ? TraceIdCodec.DisplayId(row.SessionKey) : null,
            row.AgentLabel, row.Schedule, row.Status,
            row.TotalCostUsd, row.StartedAt, row.CompletedAt,
            row.CostProvenance ?? Cron.Run.CostProvenance.Unavailable,
            deletedAt);

    public static string GetJsonString(List<CronSessionDto> entries)
        => JsonSerializer.Serialize(entries, ScopeJsonSerializerContext.Default.ListCronSessionDto);
}

/// <summary>
/// Result of a cron-agent removal, reported as JSON by the server cron API.
/// </summary>
public sealed record CronRemovedDto(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("removed")] bool Removed)
{
    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CronRemovedDto);
}

/// <summary>
/// JSON request body for creating a cron agent through the server cron API. Mirrors
/// <see cref="CronCreateRequest"/> with snake_case wire names; <see cref="ToCreateRequest"/>
/// maps it into the core request the service validates.
/// </summary>
public sealed record CronCreateRequestDto(
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("repo_path")] string? RepoPath,
    [property: JsonPropertyName("harness")] string? Harness,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("token_budget")] int? TokenBudget,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("block_concurrent_runs")] bool? BlockConcurrentRuns = null,
    [property: JsonPropertyName("max_runtime_seconds")] int? MaxRuntimeSeconds = null)
{
    public CronCreateRequest ToCreateRequest()
        => new(
            Label ?? "",
            Schedule ?? "",
            Prompt ?? "",
            RepoPath ?? "",
            Harness ?? "",
            Model,
            TokenBudget,
            Enabled ?? true,
            BlockConcurrentRuns ?? true,
            MaxRuntimeSeconds);
}

/// <summary>
/// JSON request body for updating (FULL REPLACE) a cron agent through PUT /api/cron/agents/{label}
/// (T-2026-06-17-05). Mirrors <see cref="CronCreateRequestDto"/>'s editable-field shape with
/// snake_case wire names, MINUS <c>label</c> (the label is the immutable URL path key, not a body
/// field) and MINUS <c>enabled</c> (the enabled flag stays on the enable/disable endpoints).
/// <see cref="ToUpdateRequest"/> maps it into the core request the service validates with the SAME
/// validator as create.
/// </summary>
public sealed record CronUpdateRequestDto(
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("repo_path")] string? RepoPath,
    [property: JsonPropertyName("harness")] string? Harness,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("token_budget")] int? TokenBudget,
    [property: JsonPropertyName("block_concurrent_runs")] bool? BlockConcurrentRuns = null,
    [property: JsonPropertyName("max_runtime_seconds")] int? MaxRuntimeSeconds = null)
{
    public CronUpdateRequest ToUpdateRequest()
        => new(
            Schedule ?? "",
            Prompt ?? "",
            RepoPath ?? "",
            Harness ?? "",
            Model,
            TokenBudget,
            BlockConcurrentRuns ?? true,
            MaxRuntimeSeconds);
}

/// <summary>
/// Result of a cron run-now attempt, reported as JSON by the server cron API. Carries the run id,
/// dispatch id, session key, terminal status, exit code, and any skip/failure/capability detail so
/// the frontend can deep-link into the trace and explain a degraded harness.
/// <para>
/// <c>cost_provenance</c> is the 6-value cost provenance string (wire_exact | joined_exact |
/// provider_estimated | scope_estimated | unmetered | unavailable). Null in the run record maps
/// to 'unavailable' so a null cost is NEVER serialized as "joined_exact".
/// </para>
/// </summary>
public sealed record CronRunResultDto(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("cron_agent_id")] string CronAgentId,
    [property: JsonPropertyName("dispatch_id")] string? DispatchId,
    [property: JsonPropertyName("session_key")] string? SessionKey,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("total_cost_usd")] double? TotalCostUsd,
    [property: JsonPropertyName("launched")] bool Launched,
    [property: JsonPropertyName("skip_reason")] string? SkipReason,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("capability_detail")] string? CapabilityDetail,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance)
{
    public static CronRunResultDto From(CronRunOutcome outcome)
    {
        var run = outcome.Run;
        return new CronRunResultDto(
            run.Id, run.CronAgentId, run.DispatchId, run.SessionKey, run.Status,
            run.ExitCode, run.TotalCostUsd, outcome.Launched, run.SkipReason,
            run.ErrorMessage, outcome.CapabilityDetail,
            run.CostProvenance ?? Cron.Run.CostProvenance.Unavailable);
    }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CronRunResultDto);
}

/// <summary>
/// Capability of one supported harness, reported as JSON by the server cron API for frontend
/// display. <see cref="Status"/> is the <c>HarnessCapabilityStatus</c> name; <see cref="Detail"/> is
/// an actionable human-readable string.
/// </summary>
public sealed record CronCapabilityDto(
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail)
{
    public static string GetJsonString(List<CronCapabilityDto> entries)
        => JsonSerializer.Serialize(entries, ScopeJsonSerializerContext.Default.ListCronCapabilityDto);
}

/// <summary>
/// Combined cron status payload for the <c>scope cron status</c> CLI command: the persisted
/// agent definitions plus each supported harness's capability. Carries no supervisor or
/// enable/disable runtime state (CR-M2 scope).
/// </summary>
public sealed record CronStatusDto(
    [property: JsonPropertyName("agents")] List<CronAgentDto> Agents,
    [property: JsonPropertyName("harnesses")] List<CronCapabilityDto> Harnesses)
{
    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CronStatusDto);
}

#pragma warning restore MA0016
