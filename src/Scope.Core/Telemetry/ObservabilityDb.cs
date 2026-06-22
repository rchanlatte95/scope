namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Retrieval.Search;
using Scope.Core.Utilities;
using System.Globalization;

/// <summary>
/// SQLite-backed CRUD layer for the obs_* observability tables in .scope/telemetry.db.
/// Follows the same pattern as <see cref="TelemetryDb"/>.
/// </summary>
public class ObservabilityDb(SqliteConnection connection)
{
    /// <summary>
    /// Inserts a request record. The caller provides the UUIDv7 id via the context.
    /// </summary>
    public void InsertRequest(
        string id,
        string? parentRequestId,
        string callerSurface,
        string operation,
        string? agentSessionKey,
        string? queryText,
        string? parametersJson,
        string? dispatchId,
        string status,
        int? exitCode,
        string? errorMessage,
        long? totalElapsedMs,
        DateTime createdAt,
        DateTime? completedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_requests
                (id, parent_request_id, caller_surface, operation, agent_session_key,
                 query_text, parameters_json, dispatch_id, status, exit_code, error_message,
                 total_elapsed_ms, created_at, completed_at)
            VALUES ($id, $parentId, $surface, $operation, $agent,
                    $query, $params, $dispatchId, $status, $exitCode, $error,
                    $elapsed, $createdAt, $completedAt);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$parentId", (object?)parentRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$surface", callerSurface);
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$agent", (object?)agentSessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$query", (object?)queryText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$params", (object?)parametersJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dispatchId", (object?)dispatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$exitCode", exitCode.HasValue ? exitCode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$elapsed", totalElapsedMs.HasValue ? totalElapsedMs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("o"));
        cmd.Parameters.AddWithValue("$completedAt", completedAt.HasValue ? completedAt.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts a dispatch record for correlating multiple agent requests.
    /// </summary>
    public void InsertDispatch(string id, string? sessionKey, string? label, DateTime createdAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_dispatches (id, session_key, label, created_at)
            VALUES ($id, $sessionKey, $label, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sessionKey", (object?)sessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Marks a dispatch as completed by setting its completed_at timestamp.
    /// </summary>
    public void CompleteDispatch(string id, DateTime completedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE obs_dispatches SET completed_at = $completedAt WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$completedAt", completedAt.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts all stage records from an ObservabilityContext.
    /// When <paramref name="existingTransaction"/> is null, wraps the batch in its own transaction.
    /// </summary>
    public void InsertStages(string requestId, IReadOnlyList<StageRecord> stages,
        SqliteTransaction? existingTransaction = null)
    {
        if (stages.IsNullOrEmpty) return;

        var txn = existingTransaction ?? connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO obs_request_stages
                    (id, request_id, stage_name, stage_order, elapsed_ms, metadata_json, created_at)
                VALUES ($id, $requestId, $name, $order, $elapsed, $metadata, $createdAt);
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pRequestId = cmd.Parameters.Add("$requestId", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);
            var pElapsed = cmd.Parameters.Add("$elapsed", SqliteType.Real);
            var pMetadata = cmd.Parameters.Add("$metadata", SqliteType.Text);
            var pCreatedAt = cmd.Parameters.Add("$createdAt", SqliteType.Text);

            pRequestId.Value = requestId;
            foreach (var stage in stages)
            {
                pId.Value = Guid.GetGuid7String();
                pName.Value = stage.Name;
                pOrder.Value = stage.Order;
                pElapsed.Value = stage.ElapsedMs;
                pMetadata.Value = (object?)stage.MetadataJson ?? DBNull.Value;
                pCreatedAt.Value = stage.CreatedAt.ToString("o");
                // Single-statement retry: each row INSERT is atomic and carries a unique guid id, so a
                // transient-lock retry of THIS one statement never double-applies. We do NOT retry the
                // surrounding transaction/loop as a unit (that could re-insert already-committed rows).
                cmd.ExecuteNonQueryRetrying();
            }

            if (existingTransaction is null)
                txn.Commit();
        }
        finally
        {
            if (existingTransaction is null)
                txn.Dispose();
        }
    }

    /// <summary>
    /// Inserts retrieval results linked to an obs_requests row.
    /// When <paramref name="existingTransaction"/> is null, wraps the batch in its own transaction.
    /// </summary>
    public void InsertRetrievalResults(string requestId, IReadOnlyList<RetrievalResult> results,
        string queryText, SqliteTransaction? existingTransaction = null)
    {
        if (results.IsNullOrEmpty) return;

        var txn = existingTransaction ?? connection.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow.ToString("o");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO obs_retrieval_results
                    (id, request_id, document_key, rank, score,
                     provider_source, is_exact_match, boosts_applied, created_at)
                VALUES ($id, $requestId, $docKey, $rank, $score,
                        $source, $isExact, $boosts, $createdAt);
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pRequestId = cmd.Parameters.Add("$requestId", SqliteType.Text);
            var pDocKey = cmd.Parameters.Add("$docKey", SqliteType.Text);
            var pRank = cmd.Parameters.Add("$rank", SqliteType.Integer);
            var pScore = cmd.Parameters.Add("$score", SqliteType.Real);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
            var pIsExact = cmd.Parameters.Add("$isExact", SqliteType.Integer);
            var pBoosts = cmd.Parameters.Add("$boosts", SqliteType.Text);
            var pCreatedAt = cmd.Parameters.Add("$createdAt", SqliteType.Text);

            pRequestId.Value = requestId;
            pCreatedAt.Value = now;
            foreach (var r in results)
            {
                pId.Value = Guid.GetGuid7String();
                pDocKey.Value = r.DocumentKey;
                pRank.Value = r.Rank;
                pScore.Value = r.Score;
                pSource.Value = r.Source.ToString();
                pIsExact.Value = RetrievalResultAnalysis.IsExactMatch(r, queryText) ? 1 : 0;
                pBoosts.Value = RetrievalResultAnalysis.ExtractBoosts(r);
                // Single-statement retry: each row INSERT is atomic with a unique guid id; retrying THIS
                // statement is safe and never re-applies the loop/transaction as a whole.
                cmd.ExecuteNonQueryRetrying();
            }

            if (existingTransaction is null)
                txn.Commit();
        }
        finally
        {
            if (existingTransaction is null)
                txn.Dispose();
        }
    }

    /// <summary>
    /// Inserts packing metrics for a context pack operation.
    /// </summary>
    public void InsertPackingMetrics(string requestId, PackingMetrics m)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_packing_metrics
                (id, request_id, target_fqn, budget_requested, budget_used, budget_remaining,
                 symbols_considered, symbols_included, symbols_rejected,
                 compression_ct, full_body_ct, signature_only_ct, call_site_ct,
                 dedup_removed_ct, free_symbol_ct, test_callers_capped,
                 target_token_count, context_token_count, created_at)
            VALUES ($id, $requestId, $fqn, $budgetReq, $budgetUsed, $budgetRem,
                    $considered, $included, $rejected,
                    $compression, $fullBody, $sigOnly, $callSite,
                    $dedup, $free, $testCapped,
                    $targetTokens, $contextTokens, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$requestId", requestId);
        cmd.Parameters.AddWithValue("$fqn", m.TargetFqn);
        cmd.Parameters.AddWithValue("$budgetReq", m.BudgetRequested);
        cmd.Parameters.AddWithValue("$budgetUsed", m.BudgetUsed);
        cmd.Parameters.AddWithValue("$budgetRem", m.BudgetRemaining);
        cmd.Parameters.AddWithValue("$considered", m.SymbolsConsidered);
        cmd.Parameters.AddWithValue("$included", m.SymbolsIncluded);
        cmd.Parameters.AddWithValue("$rejected", m.SymbolsRejected);
        cmd.Parameters.AddWithValue("$compression", m.CompressionCt);
        cmd.Parameters.AddWithValue("$fullBody", m.FullBodyCt);
        cmd.Parameters.AddWithValue("$sigOnly", m.SignatureOnlyCt);
        cmd.Parameters.AddWithValue("$callSite", m.CallSiteCt);
        cmd.Parameters.AddWithValue("$dedup", m.DedupRemovedCt);
        cmd.Parameters.AddWithValue("$free", m.FreeSymbolCt);
        cmd.Parameters.AddWithValue("$testCapped", m.TestCallersCapped);
        cmd.Parameters.AddWithValue("$targetTokens", m.TargetTokenCount);
        cmd.Parameters.AddWithValue("$contextTokens", m.ContextTokenCount);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts a cost estimate for a request.
    /// </summary>
    public void InsertCostEstimate(
        string requestId,
        string modelId,
        string provider,
        int inputTokens,
        int estimatedOutputTokens,
        double inputCostUsd,
        double outputCostUsd,
        double totalCostUsd)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_cost_estimates
                (id, request_id, model_id, provider, input_tokens,
                 estimated_output_tokens, input_cost_usd, output_cost_usd,
                 total_cost_usd, created_at)
            VALUES ($id, $requestId, $model, $provider, $inputTokens,
                    $outputTokens, $inputCost, $outputCost,
                    $totalCost, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$requestId", requestId);
        cmd.Parameters.AddWithValue("$model", modelId);
        cmd.Parameters.AddWithValue("$provider", provider);
        cmd.Parameters.AddWithValue("$inputTokens", inputTokens);
        cmd.Parameters.AddWithValue("$outputTokens", estimatedOutputTokens);
        cmd.Parameters.AddWithValue("$inputCost", inputCostUsd);
        cmd.Parameters.AddWithValue("$outputCost", outputCostUsd);
        cmd.Parameters.AddWithValue("$totalCost", totalCostUsd);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Upserts an agent session, updating last_seen_at and incrementing request_count.
    /// <para>
    /// <paramref name="ccSessionId"/> (T-2026-06-19-01 Phase 3b D3 bridge) is Claude Code's OWN
    /// session id (the value carried on the wire as X-Claude-Code-Session-Id, recorded on the row so
    /// the header-based wire attribution pass can bind interactive wire rows to this Scope session).
    /// It is SET-ONCE via COALESCE so a later upsert never clobbers a first-seen binding, mirroring
    /// the origin/parent-session-key set-once semantics. Null on cron and on hosts that supply no
    /// session id; the schema column is nullable and legacy rows stay NULL.
    /// </para>
    /// <para>
    /// <paramref name="baseSessionKey"/> and <paramref name="generation"/> (T-2026-06-19-03 Wave 1)
    /// stamp the session row with its session-generation provenance: the durable launcher BASE key and
    /// the per-base generation ordinal the EFFECTIVE <paramref name="sessionKey"/> was derived from. Both
    /// are SET-ONCE via COALESCE (a generation never re-points). Null on cron/MCP/host-provided/legacy
    /// rows: a NULL generation is read as 0 and a NULL base as the session_key itself (forward-only).
    /// </para>
    /// </summary>
    public void UpsertAgentSession(string sessionKey, string agentType, string agentSubtype, string? parentSessionKey = null, string? origin = null, string? ccSessionId = null, string? baseSessionKey = null, int? generation = null, string? conversationId = null, string? machineId = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_agent_sessions
                (session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count, parent_session_key, origin, cc_session_id, base_session_key, generation, conversation_id, machine_id)
            VALUES ($key, $type, $subtype, $now, $now, 1, $parentKey, $origin, $ccSessionId, $baseKey, $generation, $conversationId, $machineId)
            ON CONFLICT(session_key) DO UPDATE SET
                agent_subtype = $subtype,
                last_seen_at = $now,
                request_count = request_count + 1,
                parent_session_key = COALESCE(obs_agent_sessions.parent_session_key, $parentKey),
                origin = COALESCE(obs_agent_sessions.origin, $origin),
                cc_session_id = COALESCE(obs_agent_sessions.cc_session_id, $ccSessionId),
                base_session_key = COALESCE(obs_agent_sessions.base_session_key, $baseKey),
                generation = COALESCE(obs_agent_sessions.generation, $generation),
                conversation_id = COALESCE(obs_agent_sessions.conversation_id, $conversationId),
                machine_id = COALESCE(obs_agent_sessions.machine_id, $machineId);
            """;
        cmd.Parameters.AddWithValue("$key", sessionKey);
        cmd.Parameters.AddWithValue("$type", agentType);
        cmd.Parameters.AddWithValue("$subtype", agentSubtype);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$parentKey", (object?)parentSessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origin", (object?)origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccSessionId", (object?)ccSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$baseKey", (object?)baseSessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conversationId", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$machineId", (object?)machineId ?? DBNull.Value);
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts an agent lifecycle event.
    /// </summary>
    public void InsertAgentEvent(
        string sessionKey,
        string? requestId,
        string eventType,
        string? eventDataJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_agent_events
                (id, session_key, request_id, event_type, event_data_json, created_at)
            VALUES ($id, $session, $requestId, $eventType, $eventData, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$session", sessionKey);
        cmd.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eventType", eventType);
        cmd.Parameters.AddWithValue("$eventData", (object?)eventDataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts a payload record (inbound or outbound).
    /// </summary>
    public void InsertPayload(
        string requestId,
        string direction,
        string contentType,
        string payloadText,
        int? tokenCount)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_payloads
                (id, request_id, direction, content_type, payload_text, token_count, created_at)
            VALUES ($id, $requestId, $direction, $contentType, $payload, $tokens, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$requestId", requestId);
        cmd.Parameters.AddWithValue("$direction", direction);
        cmd.Parameters.AddWithValue("$contentType", contentType);
        cmd.Parameters.AddWithValue("$payload", payloadText);
        cmd.Parameters.AddWithValue("$tokens", tokenCount.HasValue ? tokenCount.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Inserts an agent span record for tracking hierarchical agent execution.
    /// The caller provides the pre-generated <paramref name="id"/> and pre-formatted timestamps.
    /// </summary>
    public void InsertAgentSpan(
        string id,
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
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_agent_spans
                (id, session_key, dispatch_id, parent_span_id, agent_type, agent_name,
                 started_at, completed_at, input_summary, output_summary, status,
                 agent_ref, total_tokens, input_tokens, output_tokens,
                 pinned_cost_usd, pinned_model_id, pinned_prices_as_of, pinned_breakdown_json, model)
            VALUES ($id, $sessionKey, $dispatchId, $parentSpanId, $agentType, $agentName,
                    $startedAt, $completedAt, $inputSummary, $outputSummary, $status,
                    $agentRef, $totalTokens, $inputTokens, $outputTokens,
                    $pinnedCostUsd, $pinnedModelId, $pinnedPricesAsOf, $pinnedBreakdownJson, $model);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sessionKey", sessionKey);
        cmd.Parameters.AddWithValue("$dispatchId", (object?)dispatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parentSpanId", (object?)parentSpanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agentType", agentType);
        cmd.Parameters.AddWithValue("$agentName", agentName);
        cmd.Parameters.AddWithValue("$startedAt", startedAt);
        cmd.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inputSummary", (object?)inputSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outputSummary", (object?)outputSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$agentRef", (object?)agentRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalTokens", totalTokens.HasValue ? totalTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$inputTokens", inputTokens.HasValue ? inputTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$outputTokens", outputTokens.HasValue ? outputTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$pinnedCostUsd", pinnedCostUsd.HasValue ? pinnedCostUsd.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$pinnedModelId", (object?)pinnedModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pinnedPricesAsOf", (object?)pinnedPricesAsOf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pinnedBreakdownJson", (object?)pinnedBreakdownJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Returns the ID of the most recent span in the session whose agent_ref equals the given
    /// host-assigned agent reference, or null if none exists. Used for ambient sub-agent
    /// linkage: payloads carrying agent_id link back to the span created at dispatch time.
    /// </summary>
    public string? GetSpanIdByAgentRef(string sessionKey, string agentRef)
    {
        using var cmd = connection.CreateCommand();
        // Secondary "id DESC" key (nice-to-have d): when two spans share the same agent_ref and
        // the same started_at (hook spans frequently carry identical second-resolution timestamps),
        // ORDER BY started_at alone leaves the winner SQLite-implementation-defined. Tie-breaking on
        // id makes the "most recent match" deterministic across runs and engines.
        cmd.CommandText = """
            SELECT id FROM obs_agent_spans
            WHERE session_key = $sessionKey AND agent_ref = $agentRef
            ORDER BY started_at DESC, id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sessionKey", sessionKey);
        cmd.Parameters.AddWithValue("$agentRef", agentRef);
        return cmd.ExecuteScalarRetrying() as string;
    }

    /// <summary>
    /// Inserts an agent lifecycle event with an optional span association.
    /// Same as <see cref="InsertAgentEvent"/> but includes the <c>span_id</c> column and the optional
    /// tool-call <paramref name="status"/> column (schema v14, OBS-12): the heuristic outcome
    /// ("success"/"failed") for tool_call events, NULL for every other event type or when undeterminable.
    /// </summary>
    public void InsertAgentEventWithSpan(
        string sessionKey,
        string? requestId,
        string eventType,
        string? eventDataJson,
        string? spanId,
        string? status = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO obs_agent_events
                (id, session_key, request_id, event_type, event_data_json, span_id, status, created_at)
            VALUES ($id, $session, $requestId, $eventType, $eventData, $spanId, $status, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$session", sessionKey);
        cmd.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eventType", eventType);
        cmd.Parameters.AddWithValue("$eventData", (object?)eventDataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spanId", (object?)spanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQueryRetrying();
    }

    /// <summary>
    /// Returns the ID of the most recent active span for the given session, or null if none exists.
    /// </summary>
    public string? GetActiveSpanId(string sessionKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM obs_agent_spans
            WHERE session_key = $sessionKey AND status = 'active'
            ORDER BY started_at DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sessionKey", sessionKey);
        return cmd.ExecuteScalarRetrying() as string;
    }

    /// <summary>
    /// Inserts a PRIVILEGED user_submit event (schema v10): an obs_agent_events row with
    /// <c>event_type='user_submit'</c>, the ratified <c>event_subtype</c>, the submitted content
    /// in <c>event_data_json</c>, and the dedup identity (<c>content_hash</c>, <c>turn_index</c>).
    ///
    /// DEDUP RULE: at most one user_submit per <c>(session_key, content_hash)</c>. The hash is the
    /// stable SHA-256 over the trimmed content (<see cref="UserSubmitEvent.ComputeContentHash"/>),
    /// so the same human submit captured by multiple redundant paths (payload/hook/cron) records
    /// once. The insert is guarded by a NOT EXISTS check AND backstopped by the partial UNIQUE index
    /// <c>idx_obsagent_usersubmit_dedup</c>. Returns <c>true</c> when a new row was written, <c>false</c>
    /// when the submit was already recorded (deduped). The content is stored verbatim (it stays local;
    /// it is never uplinked).
    /// </summary>
    public bool InsertUserSubmit(UserSubmitEvent submit, string? spanId = null)
    {
        ArgumentNullException.ThrowIfNull(submit);
        var contentHash = submit.ResolveContentHash();

        using var cmd = connection.CreateCommand();
        // INSERT ... SELECT ... WHERE NOT EXISTS is a single atomic statement: the existence probe and
        // the insert evaluate against the same snapshot, so concurrent duplicate submits collapse (the
        // UNIQUE index is the final backstop). rowid count after tells us whether a row was written.
        cmd.CommandText = """
            INSERT INTO obs_agent_events
                (id, session_key, request_id, event_type, event_subtype,
                 event_data_json, content_hash, turn_index, span_id, created_at)
            SELECT $id, $session, $requestId, 'user_submit', $subtype,
                   $content, $contentHash, $turnIndex, $spanId, $createdAt
            WHERE NOT EXISTS (
                SELECT 1 FROM obs_agent_events
                WHERE session_key = $session
                  AND content_hash = $contentHash
                  AND event_type = 'user_submit'
            );
            """;
        cmd.Parameters.AddWithValue("$id", Guid.GetGuid7String());
        cmd.Parameters.AddWithValue("$session", submit.SessionKey);
        cmd.Parameters.AddWithValue("$requestId", (object?)submit.RequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subtype", submit.Subtype);
        cmd.Parameters.AddWithValue("$content", submit.Content);
        cmd.Parameters.AddWithValue("$contentHash", contentHash);
        cmd.Parameters.AddWithValue("$turnIndex", submit.TurnIndex.HasValue ? submit.TurnIndex.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$spanId", (object?)spanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQueryRetrying() > 0;
    }

    /// <summary>
    /// Returns the count of <c>user_submit</c> events already recorded for a session. Used by the
    /// prompt-hook write path (capture path B) to distinguish the initiating prompt (count == 0 before
    /// this insert) from follow-ups (count > 0): the UserPromptSubmit hook payload alone carries no
    /// turn ordinal, so session state is the proof-correct discriminator rather than a guess.
    /// </summary>
    public int CountUserSubmits(string sessionKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM obs_agent_events
            WHERE session_key = $session AND event_type = 'user_submit';
            """;
        cmd.Parameters.AddWithValue("$session", sessionKey);
        return Convert.ToInt32(cmd.ExecuteScalarRetrying(), CultureInfo.InvariantCulture);
    }

}
