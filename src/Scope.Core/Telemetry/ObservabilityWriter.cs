namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Cron.Launch;
using Scope.Core.Retrieval.Search;
using Scope.Core.Utilities;
using Serilog;

/// <summary>
/// High-level writer for the obs_* observability tables in .scope/telemetry.db.
/// All write methods are wrapped in try/catch so observability failures never
/// disrupt the caller's operation.
/// </summary>
public sealed class ObservabilityWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ObservabilityDb _db;
    private readonly bool _ownsConnection;
    private bool _disposed;

    /// <summary>
    /// Opens or creates telemetry.db in the given .scope/ directory and ensures the obs_* schema exists.
    /// After EnsureSchema, runs <see cref="BackfillScopeConversationIds"/> once to fill any
    /// pre-existing sessions that have conversation_id + machine_id but no scope_conversation_id
    /// (idempotent; safe to re-run on every open).
    /// </summary>
    public ObservabilityWriter(string scopeDirectory)
    {
        var telemetryDbPath = Path.Combine(scopeDirectory, "telemetry.db");
        _connection = SQLite.OpenDb(telemetryDbPath);
        _ownsConnection = true;
        new ObservabilitySchemaManager(_connection).EnsureSchema();
        _db = new ObservabilityDb(_connection);
        BackfillScopeConversationIds();
    }

    /// <summary>
    /// Uses an existing connection (e.g., shared with TelemetryWriter to avoid double open/schema-check).
    /// The caller retains ownership of the connection.
    /// After EnsureSchema, runs <see cref="BackfillScopeConversationIds"/> once (idempotent).
    /// </summary>
    public ObservabilityWriter(SqliteConnection connection)
    {
        _connection = connection;
        _ownsConnection = false;
        new ObservabilitySchemaManager(_connection).EnsureSchema();
        _db = new ObservabilityDb(_connection);
        BackfillScopeConversationIds();
    }

    /// <summary>
    /// Records a completed request with all its stage timings.
    /// </summary>
    public void RecordRequest(
        ObservabilityContext ctx,
        string? queryText,
        string? parametersJson,
        int exitCode,
        string? errorMessage,
        long totalElapsedMs)
    {
        if (_disposed) return;
        try
        {
            using var txn = _connection.BeginTransaction();

            var status = exitCode == 0 ? "completed" : "failed";
            _db.InsertRequest(
                ctx.RequestId,
                ctx.ParentRequestId,
                ctx.Surface.ToString(),
                ctx.Operation,
                ctx.AgentSessionKey,
                queryText,
                parametersJson,
                ctx.DispatchId,
                status,
                exitCode,
                errorMessage,
                totalElapsedMs,
                ctx.CreatedAt,
                DateTime.UtcNow);

            _db.InsertStages(ctx.RequestId, ctx.GetStages(), txn);

            if (ctx.AgentSessionKey != null)
                _db.UpsertAgentSession(ctx.AgentSessionKey, ctx.Surface.ToString(), ctx.Operation, ctx.ParentSessionKey);

            txn.Commit();
        }
        catch (Exception ex)
        {
            Log.Warning("Observability request write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Records a new dispatch for correlating multiple agent requests.
    /// </summary>
    public void RecordDispatch(string id, string? sessionKey, string? label)
    {
        if (_disposed) return;
        try { _db.InsertDispatch(id, sessionKey, label, DateTime.UtcNow); }
        catch (Exception ex) { Log.Warning("Dispatch record failed (non-fatal): {ErrorMessage}", ex.Message); }
    }

    /// <summary>
    /// Marks a dispatch as completed.
    /// </summary>
    public void CompleteDispatch(string id)
    {
        if (_disposed) return;
        try { _db.CompleteDispatch(id, DateTime.UtcNow); }
        catch (Exception ex) { Log.Warning("Dispatch complete failed (non-fatal): {ErrorMessage}", ex.Message); }
    }

    /// <summary>
    /// Records retrieval results for a search operation.
    /// </summary>
    public void RecordRetrievalResults(string requestId, IReadOnlyList<RetrievalResult> results, string queryText)
    {
        if (_disposed) return;
        try
        {
            using var txn = _connection.BeginTransaction();
            _db.InsertRetrievalResults(requestId, results, queryText, txn);
            txn.Commit();
        }
        catch (Exception ex)
        {
            Log.Warning("Observability retrieval results write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Records context packing metrics.
    /// </summary>
    public void RecordPackingMetrics(string requestId, PackingMetrics metrics)
    {
        if (_disposed) return;
        try
        {
            _db.InsertPackingMetrics(requestId, metrics);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability packing metrics write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Records a cost estimate for a request.
    /// </summary>
    public void RecordCostEstimate(
        string requestId,
        string modelId,
        string provider,
        int inputTokens,
        int estimatedOutputTokens,
        double inputCostUsd,
        double outputCostUsd,
        double totalCostUsd)
    {
        if (_disposed) return;
        try
        {
            _db.InsertCostEstimate(
                requestId, modelId, provider,
                inputTokens, estimatedOutputTokens,
                inputCostUsd, outputCostUsd, totalCostUsd);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability cost estimate write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Upserts an agent session and records a lifecycle event.
    /// </summary>
    public void RecordAgentEvent(
        string sessionKey,
        string agentType,
        string agentSubtype,
        string? requestId,
        string eventType,
        string? eventDataJson,
        string? origin = null)
    {
        if (_disposed) return;
        try
        {
            _db.UpsertAgentSession(sessionKey, agentType, agentSubtype, parentSessionKey: null, origin: origin);
            _db.InsertAgentEvent(sessionKey, requestId, eventType, eventDataJson);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability agent event write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Upserts an agent session and records a lifecycle event with span association and optional parent session key.
    /// <paramref name="status"/> (OBS-12, schema v14) is the heuristic tool-call outcome
    /// ("success"/"failed"/null) for tool_call events; null for all other event types.
    /// <paramref name="conversationId"/> (schema v16) is the resume-stable conversation identity
    /// (transcript filename stem, or host session_id fallback). LAST-WRITE-WINS-WHEN-PRESENT: a
    /// non-null value overwrites the stored value; null leaves it untouched. Only the tool-call
    /// path supplies a non-null value (SessionStart and UserSubmit pass null to avoid phantom binds).
    /// <paramref name="machineId"/> (schema v16) is the locally-minted stable machine id for
    /// cross-machine merge correctness. Set-once via COALESCE.
    /// When BOTH <paramref name="machineId"/> and <paramref name="conversationId"/> are non-null,
    /// this method also calls <see cref="ResolveScopeConversationId"/> to mint or look up the
    /// durable <c>scope_conversation_id</c> and stamps it on the session row (ST-07).
    /// </summary>
    public void RecordAgentEvent(
        string sessionKey,
        string agentType,
        string agentSubtype,
        string? requestId,
        string eventType,
        string? eventDataJson,
        string? spanId,
        string? parentSessionKey = null,
        string? origin = null,
        string? ccSessionId = null,
        string? baseSessionKey = null,
        int? generation = null,
        string? status = null,
        string? conversationId = null,
        string? machineId = null)
    {
        if (_disposed) return;
        try
        {
            // Resolve scope_conversation_id only when BOTH machine_id and the sustained stem are known.
            // Mirrors the tool-call-only discipline: SessionStart and UserSubmit pass conversationId=null
            // so this branch is skipped on those phantom paths (same last-write-wins discipline).
            string? scopeConversationId = null;
            if (!string.IsNullOrEmpty(machineId) && !string.IsNullOrEmpty(conversationId))
                scopeConversationId = ResolveScopeConversationId(machineId, conversationId);

            _db.UpsertAgentSession(sessionKey, agentType, agentSubtype, parentSessionKey, origin, ccSessionId, baseSessionKey, generation, conversationId, machineId, scopeConversationId);
            _db.InsertAgentEventWithSpan(sessionKey, requestId, eventType, eventDataJson, spanId, status);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability agent event write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Upserts an agent session row WITHOUT recording any lifecycle event. Used to stamp a session's
    /// honest origin at the moment the Scope-controlled creator (e.g. CronRunner) first knows about it,
    /// so a session that produces no captured hook event still carries its origin instead of staying
    /// null. Origin is set-once via COALESCE in <see cref="ObservabilityDb.UpsertAgentSession"/>, so a
    /// later hook event cannot overwrite a creation-time stamp. Never throws; failures are logged.
    /// <para>
    /// <paramref name="ccSessionId"/> (T-2026-06-19-01 Phase 3b D3 bridge) is Claude Code's own session
    /// id (the wire's X-Claude-Code-Session-Id value), recorded next to the Scope session key so the
    /// header-based wire attribution pass can bind interactive wire rows to this session. Set-once via
    /// COALESCE in <see cref="ObservabilityDb.UpsertAgentSession"/>. Null on cron / no-host-id callers.
    /// </para>
    /// When BOTH <paramref name="machineId"/> and <paramref name="conversationId"/> are non-null,
    /// this method also calls <see cref="ResolveScopeConversationId"/> and stamps the resolved
    /// <c>scope_conversation_id</c> on the session row (ST-07).
    /// </summary>
    public void RecordAgentSession(
        string sessionKey,
        string agentType,
        string agentSubtype,
        string? parentSessionKey = null,
        string? origin = null,
        string? ccSessionId = null,
        string? baseSessionKey = null,
        int? generation = null,
        string? conversationId = null,
        string? machineId = null)
    {
        if (_disposed) return;
        try
        {
            string? scopeConversationId = null;
            if (!string.IsNullOrEmpty(machineId) && !string.IsNullOrEmpty(conversationId))
                scopeConversationId = ResolveScopeConversationId(machineId, conversationId);

            _db.UpsertAgentSession(sessionKey, agentType, agentSubtype, parentSessionKey, origin, ccSessionId, baseSessionKey, generation, conversationId, machineId, scopeConversationId);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability agent session write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Records an agent span for tracking agent/sub-agent hierarchy. Returns the generated span ID.
    /// Optionally stores the host-assigned agent reference (agent_ref) of the spawned agent,
    /// exact token usage from a synchronous Agent completion (schema v5), a pinned cost
    /// snapshot (schema v6) so the rollup can read the immutable price without re-deriving, and
    /// the recorded/requested model (schema v9) so an un-metered span can be priced from the
    /// model it was launched with (Tier 2) rather than the assumed default (Tier 3).
    /// </summary>
    public string? RecordAgentSpan(
        string sessionKey,
        string? dispatchId,
        string? parentSpanId,
        string agentType,
        string agentName,
        string startedAt,
        string? completedAt,
        string? inputSummary,
        string? outputSummary,
        string status,
        string? agentRef = null,
        long? totalTokens = null,
        long? inputTokens = null,
        long? outputTokens = null,
        double? pinnedCostUsd = null,
        string? pinnedModelId = null,
        string? pinnedPricesAsOf = null,
        string? pinnedBreakdownJson = null,
        string? model = null)
    {
        if (_disposed) return null;
        try
        {
            var id = Guid.GetGuid7String();
            _db.InsertAgentSpan(id, sessionKey, dispatchId, parentSpanId, agentType, agentName,
                startedAt, completedAt, inputSummary, outputSummary, status,
                agentRef, totalTokens, inputTokens, outputTokens,
                pinnedCostUsd, pinnedModelId, pinnedPricesAsOf, pinnedBreakdownJson, model);
            return id;
        }
        catch (Exception ex)
        {
            Log.Warning("Observability agent span write failed (non-fatal): {ErrorMessage}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the most recent span in the session whose agent_ref matches the given
    /// host-assigned agent id, or null when no span matches (or the database predates
    /// schema v5). Used for ambient sub-agent linkage; never throws.
    /// </summary>
    public string? FindSpanIdByAgentRef(string sessionKey, string agentRef)
    {
        if (_disposed) return null;
        try { return _db.GetSpanIdByAgentRef(sessionKey, agentRef); }
        catch (Exception ex)
        {
            Log.Warning("Observability span lookup failed (non-fatal): {ErrorMessage}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Records a payload (inbound or outbound). Captured by default; disable with SCOPE_CAPTURE_PAYLOADS=0.
    /// </summary>
    public void RecordPayload(
        string requestId,
        string direction,
        string contentType,
        string payloadText,
        int? tokenCount)
    {
        if (_disposed) return;
        try
        {
            _db.InsertPayload(requestId, direction, contentType, payloadText, tokenCount);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability payload write failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Records a PRIVILEGED user_submit event (a human-in-the-loop intervention). Deduped per
    /// (session_key, content_hash) so the same submit captured by multiple redundant paths
    /// (payload/hook/cron) records once. Returns <c>true</c> when a new row was written, <c>false</c>
    /// when deduped or on a (non-fatal) failure. Never throws; observability never crashes callers.
    /// </summary>
    public bool RecordUserSubmit(UserSubmitEvent submit, string? spanId = null)
    {
        if (_disposed) return false;
        try
        {
            return _db.InsertUserSubmit(submit, spanId);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability user_submit write failed (non-fatal): {ErrorMessage}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Returns the count of prior <c>user_submit</c> events for a session (capture path B uses this
    /// to assign initiating_prompt vs follow_up). Returns 0 on a (non-fatal) failure; never throws.
    /// </summary>
    public int CountUserSubmits(string sessionKey)
    {
        if (_disposed) return 0;
        try
        {
            return _db.CountUserSubmits(sessionKey);
        }
        catch (Exception ex)
        {
            Log.Warning("Observability user_submit count failed (non-fatal): {ErrorMessage}", ex.Message);
            return 0;
        }
    }

    // =====================================================================
    // scope_conversation_id resolver (ST-07, T-2026-06-23-01)
    // =====================================================================

    /// <summary>
    /// Resolves the durable Scope-owned <c>scope_conversation_id</c> for a (machine_id, stem) pair
    /// using first-insert-wins semantics inside a BEGIN IMMEDIATE transaction, mirroring
    /// <see cref="BindGeneration"/> / <see cref="ResolveEffectiveSessionKey"/>.
    ///
    /// <para>
    /// On first sight: mints a uuid7, inserts <c>obs_conversations</c> + <c>obs_conversation_bindings</c>,
    /// commits, returns the new id. On re-resolve: the SELECT inside the BEGIN IMMEDIATE finds the
    /// existing row and returns it (idempotent). Mint race (concurrent inserter wins the PK): the catch
    /// branch on attempt 0 rolls back and re-SELECTs the winner's now-visible binding (copy of
    /// BindGeneration's catch-and-reselect).
    /// </para>
    ///
    /// <para>
    /// Never throws: on any unexpected failure degrades gracefully by returning null (the caller passes
    /// null to <c>UpsertAgentSession</c> which leaves the column NULL, preserving forward writes).
    /// </para>
    /// </summary>
    /// <param name="machineId">The locally-minted stable machine id.</param>
    /// <param name="stem">The conversation stem (transcript filename stem or host session id fallback).</param>
    /// <returns>The resolved scope_conversation_id, or null on failure.</returns>
    public string? ResolveScopeConversationId(string machineId, string stem)
    {
        if (_disposed) return null;
        if (string.IsNullOrEmpty(machineId) || string.IsNullOrEmpty(stem)) return null;

        try
        {
            return BindConversation(machineId, stem);
        }
        catch (Exception ex)
        {
            Log.Warning("scope_conversation_id resolver failed (non-fatal): {ErrorMessage}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Transactional allocator behind <see cref="ResolveScopeConversationId"/>. Mirrors
    /// <see cref="BindGeneration"/>: BEGIN IMMEDIATE, SELECT existing, INSERT on first sight,
    /// catch PK collision on attempt 0 and re-SELECT.
    /// </summary>
    private string BindConversation(string machineId, string stem)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var txn = _connection.BeginTransaction(deferred: false);

            var existing = _db.SelectConversationBinding(machineId, stem, txn);
            if (existing != null)
            {
                txn.Commit();
                return existing;
            }

            var newId = Guid.GetGuid7String();
            var now = DateTime.UtcNow.ToString("o");

            try
            {
                _db.InsertConversation(newId, machineId, now, txn);
                _db.InsertConversationBinding(machineId, stem, newId, now, txn);
                txn.Commit();
                return newId;
            }
            catch (SqliteException) when (attempt == 0)
            {
                // Lost the PK race to a concurrent inserter: roll back and re-SELECT.
                txn.Rollback();
            }
        }

        // Both attempts raced: read the winner's binding outside a write txn.
        return _db.SelectConversationBinding(machineId, stem, txn: null) ?? throw new InvalidOperationException(
            $"scope_conversation_id race: could not resolve binding for ({machineId}, {stem}) after 2 attempts.");
    }

    /// <summary>
    /// Relinks <paramref name="fromScopeConversationId"/> to <paramref name="toScopeConversationId"/>
    /// in a single transaction: repoints all <c>obs_conversation_bindings</c> rows that reference
    /// <paramref name="fromScopeConversationId"/> and updates matching
    /// <c>obs_agent_sessions.scope_conversation_id</c> pointers.
    ///
    /// <para>
    /// The orphaned <c>obs_conversations</c> row for <paramref name="fromScopeConversationId"/> is
    /// intentionally left in place (historical record; invisible to rollups which key on bindings).
    /// </para>
    ///
    /// <para>Never throws; failures are logged and the relink degrades to a no-op.</para>
    /// </summary>
    public void RelinkConversation(string fromScopeConversationId, string toScopeConversationId)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(fromScopeConversationId) || string.IsNullOrEmpty(toScopeConversationId)) return;
        if (string.Equals(fromScopeConversationId, toScopeConversationId, StringComparison.Ordinal)) return;

        try
        {
            using var txn = _connection.BeginTransaction();
            _db.RelinkConversationPointers(fromScopeConversationId, toScopeConversationId, txn);
            txn.Commit();
        }
        catch (Exception ex)
        {
            Log.Warning("RelinkConversation failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// One-time idempotent backfill: for existing <c>obs_agent_sessions</c> rows that have a non-null
    /// <c>conversation_id</c> AND non-null <c>machine_id</c> but a null <c>scope_conversation_id</c>,
    /// resolves (mint-or-lookup) the scope_conversation_id and sets the pointer.
    ///
    /// <para>
    /// Idempotency is guaranteed by two layers: (1) the WHERE clause in
    /// <c>ApplyConversationBackfill</c> only touches rows still carrying a null pointer; (2)
    /// <c>ResolveScopeConversationId</c> uses first-insert-wins on the binding PK, so re-running
    /// always returns the same id for the same (machine_id, stem) without minting a new one.
    /// </para>
    ///
    /// <para>
    /// Hook: called from <see cref="ObservabilityWriter(string)"/> and
    /// <see cref="ObservabilityWriter(SqliteConnection)"/> constructors after EnsureSchema so every
    /// writer instance (CLI, Server, MCP) backfills automatically on first open.
    /// </para>
    ///
    /// <para>Never throws; failures are logged.</para>
    /// </summary>
    public void BackfillScopeConversationIds()
    {
        if (_disposed) return;
        try
        {
            var pairs = _db.SelectSessionsNeedingConversationBackfill();
            foreach (var (machineId, conversationId) in pairs)
            {
                var scopeConvId = ResolveScopeConversationId(machineId, conversationId);
                if (scopeConvId != null)
                    _db.ApplyConversationBackfill(machineId, conversationId, scopeConvId);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("BackfillScopeConversationIds failed (non-fatal): {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Resolves the EFFECTIVE session key for a launcher-minted (<c>cli-{uuid7}</c>) base by binding it
    /// to the host's Claude Code session id (T-2026-06-19-03 Wave 1). A <c>/clear</c> in the
    /// <c>scope claude</c> wrapper keeps the OS process, so the launcher's base key stays frozen while
    /// Claude Code mints a NEW session_id; a previously-unseen <paramref name="ccSessionId"/> under a
    /// base is the deterministic <c>/clear</c> boundary and allocates the next GENERATION. The effective
    /// key is the BASE for generation 0 (SUFFIXLESS, so every existing single-generation/cron/MCP/
    /// host-provided session stays byte-identical) and <c>base + "#g" + n</c> for generation 1+.
    /// <para>
    /// Determinism + race-safety: generation = order-of-first-seen rank of distinct host session ids
    /// under the base, allocated inside an IMMEDIATE transaction with a PK on (base, cc). The binding is
    /// idempotent: re-resolving the same (base, cc) returns the same effective key. A concurrent inserter
    /// that wins the PK is observed on a single SELECT retry. Sub-agents inherit the parent base and run
    /// under the parent turn's cc id, so they resolve to the parent's CURRENT generation (shared cc + PK).
    /// </para>
    /// <para>
    /// No-op guards (return <paramref name="baseKey"/> unchanged, zero rows written): a base that is not
    /// a <c>cli-</c> launcher key (cron <c>cron-{uuid7}</c>, MCP, host-provided, unattributed) OR a
    /// null/empty <paramref name="ccSessionId"/>. Observability never crashes the caller: on any
    /// unexpected failure this DEGRADES to today's behavior by returning the base, never dropping the event.
    /// </para>
    /// </summary>
    /// <param name="baseKey">The pure-resolver BASE key (tier 1 <c>cli-{uuid7}</c>, or cron/host/unattributed).</param>
    /// <param name="ccSessionId">Claude Code's own session id for this event, or null when the host sent none.</param>
    /// <param name="openSource">The SessionStart source (clear/compact/resume/startup) if known, else null (Wave 1 is fallback-only).</param>
    public string ResolveEffectiveSessionKey(string baseKey, string? ccSessionId, string? openSource = null)
    {
        if (_disposed) return baseKey;

        // Guard: only launcher-minted cli- bases with a host session id can split into generations.
        // Cron (cron-{uuid7}), MCP, host-provided, and unattributed keys are byte-identical no-ops.
        if (string.IsNullOrEmpty(baseKey)
            || !baseKey.StartsWith(InteractiveHarnessLauncher.SessionKeyPrefix, StringComparison.Ordinal)
            || string.IsNullOrEmpty(ccSessionId))
        {
            return baseKey;
        }

        try
        {
            return BindGeneration(baseKey, ccSessionId, openSource);
        }
        catch (Exception ex)
        {
            // Degrade to today's behavior (the base, == g0) rather than dropping the event.
            Log.Warning("Session generation binding failed (non-fatal): {ErrorMessage}", ex.Message);
            return baseKey;
        }
    }

    /// <summary>
    /// Transactional allocator behind <see cref="ResolveEffectiveSessionKey"/>. Returns the existing
    /// binding's effective key (idempotent) or allocates the next generation under the base. Retries the
    /// SELECT once on a BUSY/constraint race (a concurrent inserter's PK row is then readable).
    /// </summary>
    private string BindGeneration(string baseKey, string ccSessionId, string? openSource)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var txn = _connection.BeginTransaction(deferred: false);

            var existing = SelectEffectiveKey(baseKey, ccSessionId, txn);
            if (existing != null)
            {
                // Promote a now-known open_source onto a fallback (NULL) binding without re-pointing the key.
                if (openSource.IsInhabited)
                    PromoteOpenSource(baseKey, ccSessionId, openSource, txn);
                txn.Commit();
                return existing;
            }

            // No-roll precedence (T-2026-06-19-04): a SessionStart carrying its OWN no-roll source on this
            // binding (compact/resume in openSource) binds to the CURRENT generation instead of rolling, so
            // the SessionStart's cc does not false-split. clear/startup (and an unknown/null source) roll per
            // the session-id-change fallback default. The prior cross-process pending-hint mechanism was
            // removed as dead: the inline openSource is the sole no-roll signal.
            var current = CurrentGeneration(baseKey, txn);
            var noRoll = current >= 0 && IsNoRollSource(openSource);

            var generation = noRoll ? current : current + 1;
            var effectiveKey = generation == 0
                ? baseKey
                : baseKey + SessionKeyResolver.GenerationDelimiter + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                // On no-roll, this writes a new (base, cc) row pointing at the EXISTING generation's
                // effective_key, so two (base, cc) rows share one generation. The (base, generation)
                // index was relaxed to NON-unique in v13 precisely for this; the PK on (base, cc) still
                // guards against a duplicate binding for the same cc.
                InsertGenerationBinding(baseKey, ccSessionId, generation, effectiveKey, openSource, txn);
                txn.Commit();
                return effectiveKey;
            }
            catch (SqliteException) when (attempt == 0)
            {
                // Lost the PK race to a concurrent inserter: roll back and re-SELECT its now-visible row.
                txn.Rollback();
            }
        }

        // Both attempts raced: read the winner's binding outside a write txn (idempotent result).
        return SelectEffectiveKey(baseKey, ccSessionId, txn: null) ?? baseKey;
    }

    private static bool IsNoRollSource(string? openSource)
        => string.Equals(openSource, "compact", StringComparison.Ordinal)
            || string.Equals(openSource, "resume", StringComparison.Ordinal);

    private string? SelectEffectiveKey(string baseKey, string ccSessionId, SqliteTransaction? txn)
    {
        using var cmd = _connection.CreateCommand();
        if (txn != null) cmd.Transaction = txn;
        cmd.CommandText = """
            SELECT effective_key FROM obs_session_generations
            WHERE base_session_key = $base AND cc_session_id = $cc LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$base", baseKey);
        cmd.Parameters.AddWithValue("$cc", ccSessionId);
        var result = cmd.ExecuteScalarRetrying();
        return result is string s && s.Length > 0 ? s : null;
    }

    /// <summary>
    /// The current (highest) generation allocated under the base, or -1 when none exists yet. The next
    /// fresh generation is this + 1; a no-roll binding reuses this value as-is.
    /// </summary>
    private int CurrentGeneration(string baseKey, SqliteTransaction txn)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = "SELECT COALESCE(MAX(generation), -1) FROM obs_session_generations WHERE base_session_key = $base;";
        cmd.Parameters.AddWithValue("$base", baseKey);
        return Convert.ToInt32(cmd.ExecuteScalarRetrying(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void InsertGenerationBinding(
        string baseKey, string ccSessionId, int generation, string effectiveKey, string? openSource, SqliteTransaction txn)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            INSERT INTO obs_session_generations
                (base_session_key, cc_session_id, generation, effective_key, opened_at, open_source)
            VALUES ($base, $cc, $gen, $key, $now, $source);
            """;
        cmd.Parameters.AddWithValue("$base", baseKey);
        cmd.Parameters.AddWithValue("$cc", ccSessionId);
        cmd.Parameters.AddWithValue("$gen", generation);
        cmd.Parameters.AddWithValue("$key", effectiveKey);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$source", (object?)(openSource.IsInhabited ? openSource : null) ?? DBNull.Value);
        // Retry only THIS single INSERT on a transient lock. A PK-conflict race (a concurrent
        // inserter wins (base, cc)) is a NON-busy SqliteException that SqliteRetry rethrows
        // immediately, so the BindGeneration `catch (SqliteException) when (attempt == 0)` re-SELECT
        // path is preserved unchanged.
        cmd.ExecuteNonQueryRetrying();
    }

    private void PromoteOpenSource(string baseKey, string ccSessionId, string? openSource, SqliteTransaction txn)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            UPDATE obs_session_generations SET open_source = $source
            WHERE base_session_key = $base AND cc_session_id = $cc AND open_source IS NULL;
            """;
        cmd.Parameters.AddWithValue("$source", (object?)openSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$base", baseKey);
        cmd.Parameters.AddWithValue("$cc", ccSessionId);
        cmd.ExecuteNonQueryRetrying();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConnection)
            _connection.Dispose();
    }
}
