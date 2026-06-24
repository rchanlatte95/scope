namespace Scope.Core.Telemetry;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;
using System.Globalization;

// Public contract preserved (T-2026-06-17 sweep, owner): this is the public Scope.Core DB-query API
// (consumed by TraceDocuments, ScopeApp, and the `scope audit` CLI). Every method returns/accepts the
// canonical loosely-typed List<Dictionary<string,object?>> row shape; changing these public return
// types to abstractions would be a public-API change. Concrete collection types preserved.
#pragma warning disable MA0016 // public Scope.Core query API; canonical loosely-typed DB-row shapes preserved

/// <summary>
/// Read-only query layer for the obs_* observability tables in .scope/telemetry.db.
/// Used by the <c>scope audit</c> CLI command.
/// </summary>
public class ObservabilityAuditDb(SqliteConnection connection)
{
    /// <summary>
    /// Queries recent requests with optional filters for surface, operation, agent, and since.
    /// </summary>
    public List<Dictionary<string, object?>> GetRecentRequests(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Surface != null)
        {
            where.Add("caller_surface = $surface");
            cmd.Parameters.AddWithValue("$surface", filters.Surface);
        }
        if (filters.Operation != null)
        {
            where.Add("operation = $operation");
            cmd.Parameters.AddWithValue("$operation", filters.Operation);
        }
        if (filters.Agent != null)
        {
            where.Add("agent_session_key = $agent");
            cmd.Parameters.AddWithValue("$agent", filters.Agent);
        }
        if (filters.Since != null)
        {
            where.Add("created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }

        var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT id, parent_request_id, caller_surface, operation, agent_session_key,
                   query_text, status, exit_code, total_elapsed_ms, created_at, completed_at
            FROM obs_requests
            {whereClause}
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Queries request stages for a specific request, ordered by stage_order.
    /// </summary>
    public List<Dictionary<string, object?>> GetStagesByRequestId(string requestId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, stage_name, stage_order, elapsed_ms, metadata_json, created_at
            FROM obs_request_stages
            WHERE request_id = $requestId
            ORDER BY stage_order;
            """;
        cmd.Parameters.AddWithValue("$requestId", requestId);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Queries packing metrics joined with obs_requests for filtering.
    /// </summary>
    public List<Dictionary<string, object?>> GetPackingMetrics(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Since != null)
        {
            where.Add("r.created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }
        if (filters.Surface != null)
        {
            where.Add("r.caller_surface = $surface");
            cmd.Parameters.AddWithValue("$surface", filters.Surface);
        }
        if (filters.Operation != null)
        {
            where.Add("r.operation = $operation");
            cmd.Parameters.AddWithValue("$operation", filters.Operation);
        }

        var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT p.id, p.request_id, p.target_fqn, p.budget_requested, p.budget_used,
                   p.budget_remaining, p.symbols_considered, p.symbols_included,
                   p.symbols_rejected, p.compression_ct, p.full_body_ct,
                   p.signature_only_ct, p.call_site_ct, p.dedup_removed_ct,
                   p.free_symbol_ct, p.test_callers_capped,
                   p.target_token_count, p.context_token_count, p.created_at
            FROM obs_packing_metrics p
            JOIN obs_requests r ON r.id = p.request_id
            {whereClause}
            ORDER BY p.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Queries agent sessions ordered by last_seen_at descending, including parent_session_key.
    /// Honors <see cref="AuditFilters.Since"/> (an optional ISO-8601 UTC cutoff): when set, only
    /// sessions whose last_seen_at is on or after it are returned (T-2026-06-13-33 retention gate,
    /// matching GetSessionsWithEventCounts / GetSessionTree). This is a read filter only -- older
    /// rows are NOT deleted and remain in the DB.
    /// </summary>
    public List<Dictionary<string, object?>> GetAgentSessions(AuditFilters filters)
    {
        var where = filters.Since != null ? "WHERE last_seen_at >= $since" : "";
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count, parent_session_key
            FROM obs_agent_sessions
            {where}
            ORDER BY last_seen_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        if (filters.Since != null) cmd.Parameters.AddWithValue("$since", filters.Since);
        try { return ReadRows(cmd); }
        catch (SqliteException)
        {
            // Fallback for databases without parent_session_key column (pre-v4)
            using var fallback = connection.CreateCommand();
            fallback.CommandText = $"""
                SELECT session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count
                FROM obs_agent_sessions
                {where}
                ORDER BY last_seen_at DESC
                LIMIT $limit;
                """;
            fallback.Parameters.AddWithValue("$limit", filters.Limit);
            if (filters.Since != null) fallback.Parameters.AddWithValue("$since", filters.Since);
            return ReadRows(fallback);
        }
    }

    /// <summary>
    /// Agent sessions with per-session event counts, span counts, cost presence, and activity
    /// status, ordered active-first then by last_seen_at / first_seen_at / session_key.
    /// Backs GET /api/trace/sessions (scope-trace-v1 sessions_list, T-2026-06-15-15).
    ///
    /// All sessions are returned so the picker can show every session (disabled when has_data=false).
    /// <c>has_data</c> = event_count &gt; 0 OR span_count &gt; 0 OR has_cost; a session with cost
    /// expenditure is NEVER disabled even when it has no displayable events or spans.
    /// <c>is_active</c> = heuristic: open dispatch or open span (completed_at IS NULL).
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only sessions
    /// whose last_seen_at is on or after this value are returned (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted -- they remain in the DB; this is a read filter only.
    ///
    /// <paramref name="from"/>/<paramref name="to"/> are optional ISO-8601 UTC bounds applied as an
    /// OVERLAP filter (T-2026-06-15-16/ST-01): a session is visible when it was active anywhere in the
    /// window, i.e. last_seen_at &gt;= from AND first_seen_at &lt;= to. Either bound is independent and
    /// optional. These coexist with <paramref name="since"/> (retention) by AND-combination.
    ///
    /// <paramref name="includeDeleted"/> controls soft-deleted visibility (T-2026-06-15-16/ST-01):
    /// when false (default) sessions with a non-null deleted_at are excluded; when true all sessions
    /// are returned and the deleted_at column is surfaced for the trash view. deleted_at is always
    /// selected so callers can render the flag.
    /// </summary>
    public List<Dictionary<string, object?>> GetSessionsWithEventCounts(
        int limit, string? since = null, string? from = null, string? to = null, bool includeDeleted = false)
    {
        // Reader-path EnsureSchema (lazy-migration reader gap): this SELECT reads the v12
        // base_session_key / generation columns, but telemetry.db is migrated lazily by writers. A live
        // db bumped to v12 on the write path but read on an older connection would fail the SELECT, so we
        // self-ensure the obs schema once first (mirrors ResolveCcSessionId / BuildLedgerRollupAggregate).
        try { new ObservabilitySchemaManager(connection).EnsureSchema(); }
        catch (SqliteException) { /* best-effort; the column-omitting fallback below still degrades safely */ }

        // The generation columns drive the base-launch rollup (Wave 2 / ST-06) and cc_session_id (v11)
        // drives the universal by_conversation grouping + the TraceSession.cc_session_id surface
        // (T-2026-06-19-04). conversation_id + machine_id (v16) drive resume-stable cross-machine
        // conversation grouping. All are selected here so the per-session ledger batch and the
        // sessions-list projection read them WITHOUT a second query. A pre-v11/v12/v16 db (EnsureSchema
        // could not apply, e.g. a read-only connection) falls back to omitting all together (column-omitting catch).
        var newCols = ", s.base_session_key, s.generation, s.cc_session_id, s.conversation_id, s.machine_id";
        var (where, parms) = BuildSessionWindowFilter(since, from, to, includeDeleted);

        List<Dictionary<string, object?>> Run(string extraCols)
        {
            using var cmd = connection.CreateCommand();
            foreach (var (name, value) in parms) cmd.Parameters.AddWithValue(name, value);
            cmd.CommandText = $"""
                SELECT s.session_key, s.agent_type, s.agent_subtype, s.parent_session_key,
                       s.first_seen_at, s.last_seen_at, s.request_count, s.deleted_at{extraCols},
                       (SELECT COUNT(*) FROM obs_agent_events e WHERE e.session_key = s.session_key) AS event_count,
                       (SELECT COUNT(*) FROM obs_agent_spans sp WHERE sp.session_key = s.session_key) AS span_count,
                       (CASE WHEN EXISTS (SELECT 1 FROM obs_cost_estimates c JOIN obs_requests r ON r.id = c.request_id WHERE r.agent_session_key = s.session_key) THEN 1 ELSE 0 END) AS has_cost,
                       (CASE WHEN EXISTS (SELECT 1 FROM obs_dispatches d WHERE d.session_key = s.session_key AND d.completed_at IS NULL)
                                 OR EXISTS (SELECT 1 FROM obs_agent_spans sp WHERE sp.session_key = s.session_key AND sp.completed_at IS NULL)
                             THEN 1 ELSE 0 END) AS is_active
                FROM obs_agent_sessions s
                {where}
                ORDER BY is_active DESC, s.last_seen_at DESC, s.first_seen_at DESC, s.request_count DESC, s.session_key ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadRows(cmd);
        }

        try { return Run(newCols); }
        catch (SqliteException) { return Run(""); /* pre-v11/v12/v16 schema: no base_session_key/generation/cc_session_id/conversation_id/machine_id columns */ }
    }

    /// <summary>
    /// Builds the shared session-window WHERE clause + parameter list used by
    /// <see cref="GetSessionsWithEventCounts"/> (and its column-omitting pre-v12 fallback) so the two
    /// query variants stay byte-identical in their filter semantics.
    /// </summary>
    private static (string Where, List<(string Name, object Value)> Parms) BuildSessionWindowFilter(
        string? since, string? from, string? to, bool includeDeleted)
    {
        var conditions = new List<string>();
        var parms = new List<(string, object)>();
        if (since != null) { conditions.Add("s.last_seen_at >= $since"); parms.Add(("$since", since)); }
        if (from != null) { conditions.Add("s.last_seen_at >= $from"); parms.Add(("$from", from)); }
        if (to != null) { conditions.Add("s.first_seen_at <= $to"); parms.Add(("$to", to)); }
        if (!includeDeleted) conditions.Add("s.deleted_at IS NULL");
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (where, parms);
    }

    /// <summary>
    /// Counts sessions matching the same filter predicate as
    /// <see cref="GetSessionsWithEventCounts(int,string?,string?,string?,bool)"/> without
    /// applying a LIMIT. Used by the paginated sessions endpoint to return an analytically
    /// correct total_count over the active filter set (T-2026-06-17-03).
    /// </summary>
    public long CountSessionsWithEventCounts(string? since = null, string? from = null, string? to = null, bool includeDeleted = false)
    {
        using var cmd = connection.CreateCommand();
        var conditions = new List<string>();
        if (since != null)
        {
            conditions.Add("s.last_seen_at >= $since");
            cmd.Parameters.AddWithValue("$since", since);
        }
        if (from != null)
        {
            conditions.Add("s.last_seen_at >= $from");
            cmd.Parameters.AddWithValue("$from", from);
        }
        if (to != null)
        {
            conditions.Add("s.first_seen_at <= $to");
            cmd.Parameters.AddWithValue("$to", to);
        }
        if (!includeDeleted)
            conditions.Add("s.deleted_at IS NULL");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT COUNT(*)
            FROM obs_agent_sessions s
            {where};
            """;
        return (long)(cmd.ExecuteScalarRetrying() ?? 0L);
    }

    /// <summary>
    /// Counts trace entries (union of obs_requests, obs_agent_spans, obs_agent_events) for a session.
    /// Applies the same per-table retention filter as <see cref="GetFullTrace"/>:
    ///   - obs_requests: created_at >= since
    ///   - obs_agent_events: created_at >= since (same column as GetToolCallEvents)
    ///   - obs_agent_spans: started_at >= since (NOT created_at; spans use started_at per GetAgentSpans)
    /// Also filters obs_agent_events to the same event types GetToolCallEvents includes
    /// (tool_call, agent_message, user_submit) so the count matches the rendered timeline.
    /// Used by the paginated events/timeline endpoint to return an analytically correct total_count
    /// over the active filter (T-2026-06-17-03). Returns 0 when the session has no obs rows.
    /// </summary>
    public long CountFullTraceEntries(string sessionKey, string? since = null)
    {
        using var cmd = connection.CreateCommand();
        var reqSince = since != null ? "AND r.created_at >= $since" : "";
        var evtSince = since != null ? "AND e.created_at >= $since" : "";
        var spnSince = since != null ? "AND s.started_at >= $since" : "";
        cmd.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM obs_requests r WHERE r.agent_session_key = $key {reqSince})
              + (SELECT COUNT(*) FROM obs_agent_events e WHERE e.session_key = $key AND e.event_type IN ('tool_call','agent_message','user_submit') {evtSince})
              + (SELECT COUNT(*) FROM obs_agent_spans s WHERE s.session_key = $key {spnSince});
            """;
        cmd.Parameters.AddWithValue("$key", sessionKey);
        if (since != null) cmd.Parameters.AddWithValue("$since", since);
        try { return (long)(cmd.ExecuteScalarRetrying() ?? 0L); }
        catch (SqliteException) { return 0L; }
    }

    /// <summary>
    /// Soft-deletes a session by stamping obs_agent_sessions.deleted_at with the current UTC time
    /// (T-2026-06-15-16/ST-01). Reversible: only the flag is set, never the session's events, spans,
    /// requests, or costs (NO cascade). Idempotent: re-stamping an already-deleted session simply
    /// overwrites the timestamp. Returns the new deleted_at string when a row was updated, or null
    /// when no session with that key exists (lets the server map a clean 404).
    /// </summary>
    public string? SoftDeleteSession(string sessionKey)
    {
        var deletedAt = DateTime.UtcNow.ToString("o");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE obs_agent_sessions
            SET deleted_at = $deletedAt
            WHERE session_key = $sessionKey;
            """;
        cmd.Parameters.AddWithValue("$deletedAt", deletedAt);
        cmd.Parameters.AddWithValue("$sessionKey", sessionKey);
        return cmd.ExecuteNonQueryRetrying() > 0 ? deletedAt : null;
    }

    /// <summary>
    /// Restores a soft-deleted session by clearing obs_agent_sessions.deleted_at
    /// (T-2026-06-15-16/ST-01). Idempotent: restoring a non-deleted session is a no-op write that
    /// still reports success when the row exists. Returns true when a row matched the key, false
    /// when no session with that key exists (lets the server map a clean 404).
    /// </summary>
    public bool RestoreSession(string sessionKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE obs_agent_sessions
            SET deleted_at = NULL
            WHERE session_key = $sessionKey;
            """;
        cmd.Parameters.AddWithValue("$sessionKey", sessionKey);
        return cmd.ExecuteNonQueryRetrying() > 0;
    }

    /// <summary>
    /// Returns a dictionary of session_key to deleted_at for the given session keys (T-2026-06-16-02).
    /// Used by <see cref="Scope.Core.App.ScopeApp.CronListSessionsAsync"/> to annotate cron session DTOs
    /// with deletion state from telemetry.db. Keys with no obs_agent_sessions row are absent from the map.
    /// Never throws: any SqliteException returns an empty map so cron listing degrades gracefully.
    /// </summary>
    public Dictionary<string, string?> GetDeletedAtForSessions(IEnumerable<string> sessionKeys)
    {
        var keys = sessionKeys.ToList();
        if (keys.Count == 0) return [];

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>();
            for (var i = 0; i < keys.Count; i++)
            {
                var p = $"$k{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, keys[i]);
            }
            cmd.CommandText = $"""
                SELECT session_key, deleted_at
                FROM obs_agent_sessions
                WHERE session_key IN ({string.Join(", ", placeholders)});
                """;
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var deletedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                result[key] = deletedAt;
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Degrade gracefully: cron list still works; rows are shown as non-deleted.
        }
        return result;
    }

    /// <summary>
    /// Returns a session tree rooted at the given session key. The root session is first,
    /// followed by all descendants in breadth-first order. Each row includes
    /// session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count,
    /// parent_session_key, and a computed _depth integer (0 for root).
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only sessions
    /// whose last_seen_at is on or after this value are included in the traversal
    /// (T-2026-06-13-33 retention gate). Rows older than the cutoff are NOT deleted;
    /// this is a read filter only.
    /// </summary>
    public List<Dictionary<string, object?>> GetSessionTree(string sessionKey, string? since = null)
    {
        // Load all sessions into memory for tree traversal
        var allSessions = new List<Dictionary<string, object?>>();
        try
        {
            using var cmd = connection.CreateCommand();
            var where = since != null ? "WHERE last_seen_at >= $since" : "";
            if (since != null) cmd.Parameters.AddWithValue("$since", since);
            cmd.CommandText = $"""
                SELECT session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count, parent_session_key
                FROM obs_agent_sessions
                {where};
                """;
            allSessions = ReadRows(cmd);
        }
        catch (SqliteException) { return []; }

        // Build parent -> children lookup
        var byKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in allSessions)
        {
            var key = Get(s, "session_key");
            if (string.Equals(key, "-", StringComparison.Ordinal)) continue;
            byKey[key] = s;
            var parentKey = Get(s, "parent_session_key");
            if (!(string.Equals(parentKey, "-", StringComparison.Ordinal)))
            {
                if (!childrenOf.TryGetValue(parentKey, out var list))
                {
                    list = [];
                    childrenOf[parentKey] = list;
                }
                list.Add(key);
            }
        }

        if (!byKey.ContainsKey(sessionKey)) return [];

        // BFS from root
        var result = new List<Dictionary<string, object?>>();
        var queue = new Queue<(string Key, int Depth)>();
        queue.Enqueue((sessionKey, 0));
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (!byKey.TryGetValue(current, out var session)) continue;

            session["_depth"] = depth;
            result.Add(session);

            if (childrenOf.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                    queue.Enqueue((child, depth + 1));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a rollup of request counts and total costs across a session and all its descendants.
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only sessions
    /// whose last_seen_at is on or after this value are included (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted; this is a read filter only.
    /// </summary>
    public Dictionary<string, object?> GetSessionRollup(string sessionKey, string? since = null)
    {
        var tree = GetSessionTree(sessionKey, since);
        if (tree.Count == 0)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["error"] = "No session found for key: " + sessionKey };

        var sessionKeys = new List<string>();
        long totalRequestCount = 0;
        foreach (var s in tree)
        {
            var key = Get(s, "session_key");
            if (!(string.Equals(key, "-", StringComparison.Ordinal))) sessionKeys.Add(key);
            if (s.TryGetValue("request_count", out var rc) && rc is long count)
                totalRequestCount += count;
            else if (rc is int intCount)
                totalRequestCount += intCount;
        }

        // Sum costs across all request IDs for these sessions
        double totalCost = 0;
        if (sessionKeys.Count > 0)
        {
            const int batchSize = 50;
            for (var i = 0; i < sessionKeys.Count; i += batchSize)
            {
                var batch = sessionKeys.GetRange(i, Math.Min(batchSize, sessionKeys.Count - i));
                using var cmd = connection.CreateCommand();
                var placeholders = new List<string>();
                for (var j = 0; j < batch.Count; j++)
                {
                    var param = $"$s{j}";
                    placeholders.Add(param);
                    cmd.Parameters.AddWithValue(param, batch[j]);
                }

                cmd.CommandText = $"""
                    SELECT COALESCE(SUM(c.total_cost_usd), 0)
                    FROM obs_cost_estimates c
                    JOIN obs_requests r ON r.id = c.request_id
                    WHERE r.agent_session_key IN ({string.Join(", ", placeholders)});
                    """;

                try
                {
                    var result = cmd.ExecuteScalarRetrying();
                    if (result is double d) totalCost += d;
                }
                catch (SqliteException) { }
            }
        }

        // Find direct children of the root
        var directChildren = new List<string>();
        foreach (var s in tree)
        {
            var parentKey = Get(s, "parent_session_key");
            if (string.Equals(parentKey, sessionKey, StringComparison.Ordinal))
                directChildren.Add(Get(s, "session_key"));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session_key"] = sessionKey,
            ["total_request_count"] = totalRequestCount,
            ["total_cost_usd"] = Math.Round(totalCost, 6),
            ["session_count"] = tree.Count,
            ["children"] = directChildren
        };
    }

    /// <summary>
    /// Queries cost estimates joined with obs_requests for filtering.
    /// </summary>
    public List<Dictionary<string, object?>> GetCostEstimates(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Operation != null)
        {
            where.Add("r.operation = $operation");
            cmd.Parameters.AddWithValue("$operation", filters.Operation);
        }
        if (filters.Since != null)
        {
            where.Add("r.created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }
        if (filters.Surface != null)
        {
            where.Add("r.caller_surface = $surface");
            cmd.Parameters.AddWithValue("$surface", filters.Surface);
        }

        var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT c.id, c.request_id, c.model_id, c.provider,
                   c.input_tokens, c.estimated_output_tokens,
                   c.input_cost_usd, c.output_cost_usd, c.total_cost_usd, c.created_at
            FROM obs_cost_estimates c
            JOIN obs_requests r ON r.id = c.request_id
            {whereClause}
            ORDER BY c.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Queries payloads with optional direction filter.
    /// </summary>
    public List<Dictionary<string, object?>> GetPayloads(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Direction != null)
        {
            where.Add("direction = $direction");
            cmd.Parameters.AddWithValue("$direction", filters.Direction);
        }
        if (filters.RequestId != null)
        {
            where.Add("request_id = $requestId");
            cmd.Parameters.AddWithValue("$requestId", filters.RequestId);
        }
        if (filters.Since != null)
        {
            where.Add("created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }

        var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT id, request_id, direction, content_type, token_count, created_at,
                   SUBSTR(payload_text, 1, 200) AS payload_preview
            FROM obs_payloads
            {whereClause}
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Returns aggregate summary: total requests, by-surface counts, by-operation counts,
    /// average elapsed_ms, total cost.
    /// </summary>
    public Dictionary<string, object?> GetSummary(AuditFilters filters)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Total requests + avg elapsed
        using (var cmd = connection.CreateCommand())
        {
            var where = new List<string>();
            if (filters.Since != null)
            {
                where.Add("created_at >= $since");
                cmd.Parameters.AddWithValue("$since", filters.Since);
            }
            var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"""
                SELECT COUNT(*) AS total_requests,
                       AVG(total_elapsed_ms) AS avg_elapsed_ms,
                       MIN(created_at) AS earliest,
                       MAX(created_at) AS latest
                FROM obs_requests
                {whereClause};
                """;
            using var reader = cmd.ExecuteReaderRetrying();
            if (reader.Read())
            {
                result["total_requests"] = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                result["avg_elapsed_ms"] = reader.IsDBNull(1) ? null : Math.Round(reader.GetDouble(1), 1);
                result["earliest_request"] = reader.IsDBNull(2) ? null : reader.GetString(2);
                result["latest_request"] = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        // By-surface counts
        using (var cmd = connection.CreateCommand())
        {
            var where = new List<string>();
            if (filters.Since != null)
            {
                where.Add("created_at >= $since");
                cmd.Parameters.AddWithValue("$since", filters.Since);
            }
            var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"""
                SELECT caller_surface, COUNT(*) AS cnt
                FROM obs_requests
                {whereClause}
                GROUP BY caller_surface
                ORDER BY cnt DESC;
                """;
            var surfaceCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
                surfaceCounts[reader.GetString(0)] = reader.GetInt64(1);
            result["by_surface"] = surfaceCounts;
        }

        // By-operation counts
        using (var cmd = connection.CreateCommand())
        {
            var where = new List<string>();
            if (filters.Since != null)
            {
                where.Add("created_at >= $since");
                cmd.Parameters.AddWithValue("$since", filters.Since);
            }
            var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"""
                SELECT operation, COUNT(*) AS cnt
                FROM obs_requests
                {whereClause}
                GROUP BY operation
                ORDER BY cnt DESC;
                """;
            var opCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
                opCounts[reader.GetString(0)] = reader.GetInt64(1);
            result["by_operation"] = opCounts;
        }

        // Total cost
        using (var cmd = connection.CreateCommand())
        {
            var where = new List<string>();
            if (filters.Since != null)
            {
                where.Add("c.created_at >= $since");
                cmd.Parameters.AddWithValue("$since", filters.Since);
            }
            var whereClause = where.IsInhabited ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"""
                SELECT COALESCE(SUM(c.total_cost_usd), 0) AS total_cost_usd
                FROM obs_cost_estimates c
                {whereClause};
                """;
            using var reader = cmd.ExecuteReaderRetrying();
            if (reader.Read())
                result["total_cost_usd"] = reader.IsDBNull(0) ? 0.0 : Math.Round(reader.GetDouble(0), 6);
        }

        return result;
    }

    /// <summary>
    /// Checks whether the obs_requests table exists in the database (i.e., observability schema is present).
    /// </summary>
    public bool HasObservabilitySchema()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='obs_requests';";
        return Convert.ToInt32(cmd.ExecuteScalarRetrying(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Returns trace entries joining requests with inbound/outbound payloads.
    /// Supports full payload or truncated (500-char) preview based on <see cref="AuditFilters.FullPayloads"/>.
    /// </summary>
    public List<Dictionary<string, object?>> GetTraceEntries(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Surface != null)
        {
            where.Add("r.caller_surface = $surface");
            cmd.Parameters.AddWithValue("$surface", filters.Surface);
        }
        if (filters.Operation != null)
        {
            where.Add("r.operation = $operation");
            cmd.Parameters.AddWithValue("$operation", filters.Operation);
        }
        if (filters.Agent != null)
        {
            where.Add("r.agent_session_key = $agent");
            cmd.Parameters.AddWithValue("$agent", filters.Agent);
        }
        if (filters.Since != null)
        {
            where.Add("r.created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }
        if (filters.RequestId != null)
        {
            where.Add("r.id = $requestId");
            cmd.Parameters.AddWithValue("$requestId", filters.RequestId);
        }
        if (filters.RunKey != null)
        {
            where.Add("r.agent_session_key = $runKey");
            cmd.Parameters.AddWithValue("$runKey", filters.RunKey);
        }
        if (filters.DispatchIdFilter != null)
        {
            where.Add("r.dispatch_id = $dispatchId");
            cmd.Parameters.AddWithValue("$dispatchId", filters.DispatchIdFilter);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var payloadSelect = filters.FullPayloads
            ? "pi.payload_text AS inbound_payload, po.payload_text AS outbound_payload"
            : "SUBSTR(pi.payload_text, 1, 500) AS inbound_payload, SUBSTR(po.payload_text, 1, 500) AS outbound_payload";

        cmd.CommandText = $"""
            SELECT r.id, r.caller_surface, r.operation, r.agent_session_key,
                   r.query_text, r.parameters_json, r.status, r.exit_code,
                   r.total_elapsed_ms, r.created_at, r.completed_at,
                   pi.content_type AS inbound_content_type, pi.token_count AS inbound_token_count,
                   {payloadSelect},
                   po.content_type AS outbound_content_type, po.token_count AS outbound_token_count
            FROM obs_requests r
            LEFT JOIN obs_payloads pi ON pi.request_id = r.id AND pi.direction = 'inbound'
            LEFT JOIN obs_payloads po ON po.request_id = r.id AND po.direction = 'outbound'
            {whereClause}
            ORDER BY r.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        return ReadRows(cmd);
    }

    /// <summary>
    /// Returns agent sessions (runs) with aggregate info, ordered by last_seen_at descending.
    /// </summary>
    public List<Dictionary<string, object?>> GetRuns(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.Since != null)
        {
            where.Add("s.last_seen_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT s.session_key, s.agent_type, s.agent_subtype, s.first_seen_at, s.last_seen_at,
                   s.request_count, s.parent_session_key,
                   (SELECT GROUP_CONCAT(DISTINCT operation) FROM obs_requests WHERE agent_session_key = s.session_key) AS operations
            FROM obs_agent_sessions s
            {whereClause}
            ORDER BY s.last_seen_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);
        try { return ReadRows(cmd); }
        catch (SqliteException)
        {
            // Fallback for pre-v4 databases
            using var fallback = connection.CreateCommand();
            var fbWhere = new List<string>();
            if (filters.Since != null)
            {
                fbWhere.Add("s.last_seen_at >= $since");
                fallback.Parameters.AddWithValue("$since", filters.Since);
            }
            var fbWhereClause = fbWhere.Count > 0 ? "WHERE " + string.Join(" AND ", fbWhere) : "";
            fallback.CommandText = $"""
                SELECT s.session_key, s.agent_type, s.agent_subtype, s.first_seen_at, s.last_seen_at,
                       s.request_count,
                       (SELECT GROUP_CONCAT(DISTINCT operation) FROM obs_requests WHERE agent_session_key = s.session_key) AS operations
                FROM obs_agent_sessions s
                {fbWhereClause}
                ORDER BY s.last_seen_at DESC
                LIMIT $limit;
                """;
            fallback.Parameters.AddWithValue("$limit", filters.Limit);
            return ReadRows(fallback);
        }
    }

    /// <summary>
    /// Returns dispatches with request counts. Gracefully returns empty if obs_dispatches table does not exist.
    /// </summary>
    public List<Dictionary<string, object?>> GetDispatches(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.RunKey != null)
        {
            where.Add("d.session_key = $runKey");
            cmd.Parameters.AddWithValue("$runKey", filters.RunKey);
        }
        if (filters.Since != null)
        {
            where.Add("d.created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT d.id, d.session_key, d.label, d.created_at, d.completed_at,
                   (SELECT COUNT(*) FROM obs_requests WHERE dispatch_id = d.id) AS request_count
            FROM obs_dispatches d
            {whereClause}
            ORDER BY d.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);

        try { return ReadRows(cmd); }
        catch (SqliteException) { return []; }
    }

    /// <summary>
    /// Queries tool_call, agent_message, and user_submit events from obs_agent_events with optional
    /// session/dispatch/since filters. user_submit events (ST-01 privileged class) are included so
    /// the FE trace timeline can render them as elevated human-intervention cards (ST-05).
    /// </summary>
    public List<Dictionary<string, object?>> GetToolCallEvents(AuditFilters filters)
    {
        // Close the lazy-migration reader gap (OBS-12, schema v14): obs_agent_events.status is selected
        // below, but telemetry.db migrates only on write paths, so a server READ against a live db last
        // written by a pre-v14 binary would be missing the column. Without this the SELECT throws and the
        // catch silently returns [] -- dropping EVERY event from the trace. EnsureSchema adds the missing
        // column once (additive, no backfill) so the read succeeds. Best-effort: a failure here degrades
        // to the existing try/catch below, never throws into the caller.
        try { new ObservabilitySchemaManager(connection).EnsureSchema(); }
        catch (SqliteException) { /* best-effort; the SELECT try/catch below still degrades safely */ }

        using var cmd = connection.CreateCommand();
        // Include user_submit alongside tool_call and agent_message so the FE receives all event
        // types that appear in the trace timeline. user_submit rows store raw content in
        // event_data_json verbatim (not a JSON object), so dispatch_id json_extract is guarded.
        var where = new List<string> { "e.event_type IN ('tool_call', 'agent_message', 'user_submit')" };
        if (filters.RunKey != null)
        {
            where.Add("e.session_key = $runKey");
            cmd.Parameters.AddWithValue("$runKey", filters.RunKey);
        }
        if (filters.DispatchIdFilter != null)
        {
            // Dispatch-scoped queries exclude user_submit: they belong to the session, not a
            // single dispatch, and their event_data_json is not a JSON object.
            where.Add("e.event_type != 'user_submit'");
            where.Add("json_valid(e.event_data_json)");
            where.Add("json_extract(e.event_data_json, '$.dispatch_id') = $dispatchId");
            cmd.Parameters.AddWithValue("$dispatchId", filters.DispatchIdFilter);
        }
        if (filters.Since != null)
        {
            where.Add("e.created_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }

        var whereClause = "WHERE " + string.Join(" AND ", where);
        // Include event_subtype and turn_index (schema v10) for user_submit projection (ST-05) and
        // status (schema v14, OBS-12) for tool-call success/failed surfacing. Pre-v10/v14 databases
        // missing these columns throw SqliteException caught below (the EnsureSchema above closes the
        // live-db reader gap for v14).
        cmd.CommandText = $"""
            SELECT e.id, e.session_key, e.event_type, e.event_subtype, e.turn_index,
                   e.event_data_json, e.created_at, e.span_id, e.status
            FROM obs_agent_events e
            {whereClause}
            ORDER BY e.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);

        try { return ReadRows(cmd); }
        catch (SqliteException) { return []; }
    }

    /// <summary>
    /// Returns the full trace for a run (agent session) with tool_call events interleaved chronologically.
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only entries whose
    /// created_at/started_at is on or after this value are returned (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted -- they remain in the DB; this is a read filter only.
    /// </summary>
    public List<Dictionary<string, object?>> GetRunTrace(string sessionKey, bool fullPayloads, string? since = null)
        => GetFullTrace(new AuditFilters(RunKey: sessionKey, FullPayloads: fullPayloads, Limit: 1000, Since: since));

    /// <summary>
    /// Resolves a dispatch ID prefix to the full dispatch ID.
    /// Returns the full ID if exactly one match, null if no match.
    /// Throws <see cref="InvalidOperationException"/> if the prefix is ambiguous (multiple matches).
    /// </summary>
    public string? ResolveDispatchPrefix(string prefix)
    {
        if (prefix.Length >= 32) return prefix;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM obs_dispatches WHERE id LIKE $prefix || '%' LIMIT 11;";
        cmd.Parameters.AddWithValue("$prefix", prefix);

        var matches = new List<string>();
        try
        {
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
                matches.Add(reader.GetString(0));
        }
        catch (SqliteException)
        {
            return null;
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Ambiguous dispatch prefix '{prefix}', matches: {string.Join(", ", matches.Select(m => m[..Math.Min(m.Length, 12)]))}")
        };
    }

    /// <summary>
    /// Returns dispatch metadata by resolved ID, or null if not found.
    /// </summary>
    public Dictionary<string, object?>? GetDispatchInfo(string dispatchId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.session_key, d.label, d.created_at, d.completed_at,
                   (SELECT COUNT(*) FROM obs_requests WHERE dispatch_id = d.id) AS request_count
            FROM obs_dispatches d
            WHERE d.id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", dispatchId);
        try
        {
            var rows = ReadRows(cmd);
            return rows.Count > 0 ? rows[0] : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the full trace for a dispatch with tool_call events interleaved chronologically.
    /// Supports prefix matching: if <paramref name="dispatchId"/> is shorter than 32 chars, resolves to the full ID.
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only entries whose
    /// created_at/started_at is on or after this value are returned (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted -- they remain in the DB; this is a read filter only.
    /// </summary>
    public List<Dictionary<string, object?>> GetDispatchTrace(string dispatchId, bool fullPayloads, string? since = null)
    {
        var resolvedId = ResolveDispatchPrefix(dispatchId);
        if (resolvedId == null) return [];
        return GetFullTrace(new AuditFilters(DispatchIdFilter: resolvedId, FullPayloads: fullPayloads, Limit: 1000, Since: since));
    }

    /// <summary>
    /// Queries agent spans from obs_agent_spans with optional session/dispatch/since filters.
    /// </summary>
    public List<Dictionary<string, object?>> GetAgentSpans(AuditFilters filters)
    {
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (filters.RunKey != null)
        {
            where.Add("s.session_key = $runKey");
            cmd.Parameters.AddWithValue("$runKey", filters.RunKey);
        }
        if (filters.DispatchIdFilter != null)
        {
            where.Add("s.dispatch_id = $dispatchId");
            cmd.Parameters.AddWithValue("$dispatchId", filters.DispatchIdFilter);
        }
        if (filters.Since != null)
        {
            where.Add("s.started_at >= $since");
            cmd.Parameters.AddWithValue("$since", filters.Since);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        // Include pinned-cost columns (schema v6). Pre-v6 databases degrade silently via the
        // try/catch: missing columns produce a SqliteException which returns the empty list,
        // but the outer GetFullTrace catch means pre-v6 spans are annotated without pinned data.
        cmd.CommandText = $"""
            SELECT s.id, s.session_key, s.dispatch_id, s.parent_span_id,
                   s.agent_type, s.agent_name, s.started_at, s.completed_at,
                   s.input_summary, s.output_summary, s.status,
                   s.pinned_cost_usd, s.pinned_model_id, s.pinned_prices_as_of, s.pinned_breakdown_json,
                   s.model
            FROM obs_agent_spans s
            {whereClause}
            ORDER BY s.started_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", filters.Limit);

        try { return ReadRows(cmd); }
        catch (SqliteException) { return []; }
    }

    /// <summary>
    /// Merges obs_requests trace entries, obs_agent_events tool_call entries, and obs_agent_spans,
    /// sorted chronologically by created_at. Each row includes a "_source" discriminator
    /// ("request", "event", or "span") for the formatter. Entries whose timestamps fall within
    /// a span's [started_at, completed_at] window are annotated with "_parent_span_id" pointing
    /// to the innermost containing span.
    /// </summary>
    public List<Dictionary<string, object?>> GetFullTrace(AuditFilters filters)
    {
        var requests = GetTraceEntries(filters);
        foreach (var r in requests) r["_source"] = "request";

        var events = GetToolCallEvents(filters);
        foreach (var e in events) e["_source"] = "event";

        var spans = GetAgentSpans(filters);
        foreach (var s in spans)
        {
            s["_source"] = "span";
            s["created_at"] = s.TryGetValue("started_at", out var startedAt) ? startedAt : null;
        }

        var merged = new List<Dictionary<string, object?>>(requests.Count + events.Count + spans.Count);
        merged.AddRange(requests);
        merged.AddRange(events);
        merged.AddRange(spans);

        merged.Sort((a, b) =>
        {
            var aTime = a.TryGetValue("created_at", out var av) ? av?.ToString() : null;
            var bTime = b.TryGetValue("created_at", out var bv) ? bv?.ToString() : null;
            return string.Compare(aTime, bTime, StringComparison.Ordinal);
        });

        AssignTemporalParents(merged);
        EnrichSpanCosts(merged);
        AnnotateWireTokens(merged);
        ResolveSpanModels(merged);

        return merged;
    }

    /// <summary>
    /// Resolves the TRUE model for every span row (OBS-11) and stamps it onto the row as
    /// <c>_resolved_model</c> + <c>_resolved_model_provenance</c>, which the span projection surfaces as
    /// the trace entry's <c>model</c> / <c>model_provenance</c>. This is AMBIENT capture (never agent
    /// self-report): an inherited-model sub-agent span persists a NULL <c>model</c> column (only
    /// explicitly model-pinned dispatches record one), so its model is recovered from the wire side here.
    /// <para>
    /// Explicit, non-fabricating fallback ladder (matches the cost ladder in TraceRollup):
    /// </para>
    /// <list type="number">
    ///   <item>Tier-1 <c>wire</c>: the proxy-metered model joined to this span by
    ///         <see cref="AnnotateWireTokens"/> (<c>_wire_model</c>), via the exact <c>cc_agent_id</c> ->
    ///         <c>agent_ref</c> join (preferred) or the <c>cc_session_id</c> session/parent fallback in
    ///         <see cref="AnnotateWireTokensByHeader"/>. This is provider ground truth.</item>
    ///   <item>Tier-2 <c>recorded</c>: the recorded/requested model on the span (<c>obs_agent_spans.model</c>,
    ///         schema v9): cron --model or a hook's tool_input.model on a model-pinned dispatch.</item>
    ///   <item>Otherwise the model is genuinely unknown: <c>model</c> stays null and provenance is
    ///         <c>unavailable</c>. NOTE this provenance is the model IDENTITY origin, not its price: an
    ///         observed-but-unpriceable model still reads <c>wire</c>/<c>recorded</c> here, while its PRICE
    ///         separately degrades to unavailable downstream so a price is never fabricated
    ///         (<see cref="Scope.Core.CostEstimation.ModelIdNormalizer.Lookup"/> returns null).</item>
    /// </list>
    /// When a span carried NO recorded model but a wire model was recovered, the Tier-1 fallback is LOGGED
    /// at debug so inherited-model recovery is observable. Pure over already-fetched rows; never throws.
    /// </summary>
    private static void ResolveSpanModels(List<Dictionary<string, object?>> entries)
    {
        foreach (var r in entries)
        {
            if (!(string.Equals(Get(r, "_source"), "span", StringComparison.Ordinal))) continue;

            var wireModel = NullIfDash(Get(r, "_wire_model"));
            var recordedModel = NullIfDash(Get(r, "model"));

            string? resolvedModel;
            string provenance;
            if (wireModel != null)
            {
                resolvedModel = wireModel;
                provenance = Scope.Core.Telemetry.Trace.CostEstimationSource.Wire;
                // Inherited-model recovery: a span with NO recorded model whose model was recovered from
                // the wire join. Log so ambient model attribution on inherited dispatches is observable.
                if (recordedModel == null)
                    Serilog.Log.Debug(
                        "OBS-11 inherited-model resolved from wire for span {SpanId}: model={Model} (no recorded span model)",
                        Get(r, "id"), wireModel);
            }
            else if (recordedModel != null)
            {
                resolvedModel = recordedModel;
                provenance = Scope.Core.Telemetry.Trace.CostEstimationSource.Recorded;
            }
            else
            {
                resolvedModel = null;
                provenance = Scope.Core.Cron.Run.CostProvenance.Unavailable;
            }

            r["_resolved_model"] = resolvedModel;
            r["_resolved_model_provenance"] = provenance;
        }
    }

    private static string? NullIfDash(string s)
        => string.Equals(s, "-", StringComparison.Ordinal) || s.Length == 0 ? null : s;

    /// <summary>
    /// Annotates each entry with "_parent_span_id" if its timestamp falls within a span's
    /// [started_at, completed_at] window. When multiple spans overlap, the narrowest
    /// (shortest duration) wins, giving innermost-span assignment. Zero-duration spans
    /// (PostToolUse hook-generated) are first widened with a synthetic read-time window
    /// via <see cref="WidenZeroDurationSpans"/> so subsequent entries can nest under them (DF-02).
    /// </summary>
    internal static void AssignTemporalParents(List<Dictionary<string, object?>> entries)
    {
        var spanWindows = new List<(string Id, DateTimeOffset Start, DateTimeOffset End, TimeSpan Duration, string Session)>();
        foreach (var entry in entries)
        {
            if (!(string.Equals(Get(entry, "_source"), "span", StringComparison.Ordinal))) continue;
            var id = Get(entry, "id");
            var startStr = Get(entry, "started_at");
            var endStr = Get(entry, "completed_at");
            if (string.Equals(id, "-", StringComparison.Ordinal) || string.Equals(startStr, "-", StringComparison.Ordinal) || string.Equals(endStr, "-", StringComparison.Ordinal)) continue;
            if (!DateTimeOffset.TryParse(startStr, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var start) || !DateTimeOffset.TryParse(endStr, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var end)) continue;
            // Carry the span's session in the tuple so the nesting loop can session-filter
            // candidates without a per-entry O(N) lookup, and so WidenZeroDurationSpans does not
            // need a separate O(N) rescan of the entry list for each zero-duration span (nice-to-have b).
            spanWindows.Add((id, start, end, end - start, SessionKeyOf(entry)));
        }

        if (spanWindows.Count == 0) return;

        WidenZeroDurationSpans(entries, spanWindows);

        foreach (var entry in entries)
        {
            var entryId = Get(entry, "id");
            var source = Get(entry, "_source");
            var timeStr = Get(entry, "created_at");
            if (string.Equals(timeStr, "-", StringComparison.Ordinal) || !DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var entryTime)) continue;

            // For span entries that already have a write-side parent_span_id, use it directly
            if (string.Equals(source, "span", StringComparison.Ordinal))
            {
                var existingParent = Get(entry, "parent_span_id");
                if (!(string.Equals(existingParent, "-", StringComparison.Ordinal)) && existingParent.Length > 0)
                {
                    entry["_parent_span_id"] = existingParent;
                    continue;
                }
            }

            // For event entries that already have a write-side span_id, use it directly
            if (string.Equals(source, "event", StringComparison.Ordinal))
            {
                var existingSpanId = Get(entry, "span_id");
                if (!(string.Equals(existingSpanId, "-", StringComparison.Ordinal)) && existingSpanId.Length > 0)
                {
                    entry["_parent_span_id"] = existingSpanId;
                    continue;
                }
            }

            // Session of the entry, normalized across sources (requests carry agent_session_key;
            // spans/events carry session_key). Confines temporal-parent candidates to the same
            // session so a span in session A can never adopt an entry from session B when their
            // windows overlap. Write-side span_id/parent_span_id links short-circuit above and are
            // unaffected by this filter.
            var entrySession = SessionKeyOf(entry);

            string? bestParentId = null;
            var bestDuration = TimeSpan.MaxValue;

            foreach (var (spanId, start, end, duration, spanSession) in spanWindows)
            {
                if (string.Equals(spanId, entryId, StringComparison.Ordinal)) continue;
                // Same-session-only: exclude a candidate span only when both sides carry a
                // session identity and they differ. Entries lacking a session (in-memory unit
                // fixtures, partially populated rows) still match, preserving prior behavior.
                if (DifferentSession(spanSession, entrySession)) continue;
                // Skip spans that are still zero-duration as temporal parent candidates --
                // they have no meaningful time window and would otherwise win "innermost"
                // since TimeSpan.Zero < any positive duration (DF-08). Hook-generated spans
                // (started_at == completed_at) normally receive a synthetic non-zero window
                // via WidenZeroDurationSpans (DF-02) and then participate like real spans.
                if (start == end) continue;
                if (entryTime >= start && entryTime <= end && duration < bestDuration)
                {
                    bestParentId = spanId;
                    bestDuration = duration;
                }
            }

            if (bestParentId != null)
                entry["_parent_span_id"] = bestParentId;
        }
    }

    /// <summary>
    /// Normalizes the session identity of a merged trace entry. Span and event rows carry
    /// <c>session_key</c>; request rows carry <c>agent_session_key</c> (there is no
    /// <c>session_key</c> column on requests), so without this normalization a request could
    /// never be session-matched and a foreign-session request inside a widened window would nest
    /// and bill to the wrong span via EnrichSpanCosts. Returns "-" when no session is present.
    /// </summary>
    private static string SessionKeyOf(Dictionary<string, object?> entry)
    {
        var key = Get(entry, "session_key");
        if (!(string.Equals(key, "-", StringComparison.Ordinal))) return key;
        return Get(entry, "agent_session_key");
    }

    /// <summary>
    /// True only when both sides carry a session identity and they differ. Either side lacking a
    /// session ("-") is treated as "matches anything" so in-memory fixtures and partially
    /// populated rows behave exactly as before the session filter was added.
    /// </summary>
    private static bool DifferentSession(string a, string b) =>
        !(string.Equals(a, "-", StringComparison.Ordinal)) && a.Length > 0 && !(string.Equals(b, "-", StringComparison.Ordinal)) && b.Length > 0 && !(string.Equals(a, b, StringComparison.Ordinal));

    // 30s: a hook-generated (zero-duration) span with no following same-session event still needs
    // SOME window to nest under. 30s is long enough to capture the tool's own immediate follow-up
    // entries within a single Claude Code turn, short enough to avoid swallowing the next unrelated
    // turn. Read-time heuristic only; never persisted (nice-to-have a).
    private const int DefaultZeroDurationWindowSeconds = 30;

    /// <summary>
    /// Widens zero-duration agent spans (started_at == completed_at, as produced by the
    /// PostToolUse hook which fires only after tool completion) with a synthetic read-time
    /// window so subsequent trace entries can nest under them (DF-02, Approach C).
    /// For each zero-duration span the chronologically sorted trace is scanned forward for
    /// same-session events strictly after the span's start: the first event that is not a
    /// write-side child of the span (span_id mismatch) bounds the synthetic window; if only
    /// write-side child events follow, the last of them bounds it; if no same-session events
    /// follow at all, the span is widened by a default window of
    /// <see cref="DefaultZeroDurationWindowSeconds"/> seconds. Read-time only: stored rows
    /// are never modified, only the in-memory windows used for temporal nesting change.
    /// <para>
    /// Correctness property (nice-to-have c): the synthetic windows produced here exist ONLY to
    /// drive parent assignment in <see cref="AssignTemporalParents"/>. Display duration must keep
    /// reading the stored <c>started_at</c>/<c>completed_at</c> columns off the entry rows
    /// (HtmlTraceFormatter.ComputeSpanDuration), never these widened span-window values; a
    /// zero-duration hook span must still render as instantaneous, not as a 30-second span.
    /// </para>
    /// </summary>
    internal static void WidenZeroDurationSpans(
        List<Dictionary<string, object?>> entries,
        List<(string Id, DateTimeOffset Start, DateTimeOffset End, TimeSpan Duration, string Session)> spanWindows)
    {
        for (var i = 0; i < spanWindows.Count; i++)
        {
            var (id, start, end, _, spanSession) = spanWindows[i];
            if (start != end) continue;

            DateTimeOffset? boundary = null;
            DateTimeOffset? lastSameSessionEvent = null;

            foreach (var entry in entries)
            {
                if (!(string.Equals(Get(entry, "_source"), "event", StringComparison.Ordinal))) continue;
                var timeStr = Get(entry, "created_at");
                if (string.Equals(timeStr, "-", StringComparison.Ordinal) || !DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var time)) continue;
                if (time <= start) continue;

                var eventSession = Get(entry, "session_key");
                if (DifferentSession(spanSession, eventSession)) continue;

                if (lastSameSessionEvent == null || time > lastSameSessionEvent) lastSameSessionEvent = time;
                if (string.Equals(Get(entry, "span_id"), id, StringComparison.Ordinal)) continue;
                if (boundary == null || time < boundary) boundary = time;
            }

            var syntheticEnd = boundary
                ?? lastSameSessionEvent
                ?? start.AddSeconds(DefaultZeroDurationWindowSeconds);
            spanWindows[i] = (id, start, syntheticEnd, syntheticEnd - start, spanSession);
        }
    }

    /// <summary>
    /// Annotates span entries with "_total_cost_usd" by summing costs of all requests
    /// temporally assigned to each span (via _parent_span_id). Costs roll up from child
    /// spans to parent spans. Spans carrying exact host-reported token usage (schema v5,
    /// from synchronous Agent completions) are additionally annotated with "_total_tokens"
    /// and "_token_source"="exact"; exact usage is preferred over the time-window estimate,
    /// which remains the fallback for spans without it.
    /// <para>
    /// Cost-vs-token rollup asymmetry (deliberate, nice-to-have e): "_total_cost_usd" rolls up
    /// the span subtree but "_total_tokens" is PER-SPAN ONLY and is never rolled up. Three reasons:
    /// (1) every leaf request has an independent cost estimate that sums cleanly, whereas exact
    /// tokens come from obs_agent_spans.total_tokens which exists only on sync Agent-completion
    /// spans -- async/dispatch/hook spans have none, so a rollup would emit a partial sum still
    /// labelled "exact", overstating confidence; (2) a parent Agent completion's host-reported
    /// total_tokens may already include its sub-agents' usage (the host does not document
    /// exclusion), so summing children into it risks silent double counting we cannot detect;
    /// (3) cost has a per-span time-window fallback, tokens do not -- reporting the exact per-span
    /// number where present is strictly more trustworthy than summing a sparse field. The
    /// scope-trace-v1 TokenBlock therefore reads "_total_tokens" as that span's own exact usage,
    /// not a subtree total. Roll exact tokens up only once the host guarantees parent totals
    /// exclude children (then this becomes a clean sum like cost).
    /// </para>
    /// </summary>
    private void EnrichSpanCosts(List<Dictionary<string, object?>> entries)
    {
        AnnotateExactSpanTokens(entries);

        var requestIds = new List<string>();
        foreach (var e in entries)
        {
            if (string.Equals(Get(e, "_source"), "request", StringComparison.Ordinal))
            {
                var id = Get(e, "id");
                if (!(string.Equals(id, "-", StringComparison.Ordinal))) requestIds.Add(id);
            }
        }

        if (requestIds.Count == 0) return;

        var costsByRequest = GetCostTotalsByRequestIds(requestIds);
        if (costsByRequest.Count == 0) return;

        // Build spanId -> direct child request costs
        var directCosts = new Dictionary<string, double>(StringComparer.Ordinal);
        // Build spanId -> child span IDs for rollup
        var childSpans = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var e in entries)
        {
            var parentSpanId = Get(e, "_parent_span_id");
            if (string.Equals(parentSpanId, "-", StringComparison.Ordinal)) continue;

            var source = Get(e, "_source");
            if (string.Equals(source, "request", StringComparison.Ordinal))
            {
                var reqId = Get(e, "id");
                if (costsByRequest.TryGetValue(reqId, out var cost))
                {
                    directCosts.TryGetValue(parentSpanId, out var existing);
                    directCosts[parentSpanId] = existing + cost;
                }
            }
            else if (string.Equals(source, "span", StringComparison.Ordinal))
            {
                var spanId = Get(e, "id");
                if (!childSpans.TryGetValue(parentSpanId, out var list))
                {
                    list = [];
                    childSpans[parentSpanId] = list;
                }
                list.Add(spanId);
            }
        }

        // Recursive rollup: span total = direct costs + sum of child span totals
        var spanTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        double ComputeTotal(string spanId)
        {
            if (spanTotals.TryGetValue(spanId, out var cached)) return cached;
            directCosts.TryGetValue(spanId, out var direct);
            var total = direct;
            if (childSpans.TryGetValue(spanId, out var children))
            {
                foreach (var child in children)
                    total += ComputeTotal(child);
            }
            spanTotals[spanId] = total;
            return total;
        }

        foreach (var e in entries)
        {
            if (!(string.Equals(Get(e, "_source"), "span", StringComparison.Ordinal))) continue;
            var spanId = Get(e, "id");
            if (string.Equals(spanId, "-", StringComparison.Ordinal)) continue;
            var total = ComputeTotal(spanId);
            if (total > 0)
                e["_total_cost_usd"] = total;
        }
    }

    /// <summary>
    /// Annotates span entries with "_total_tokens" from exact host-reported usage
    /// (obs_agent_spans.total_tokens, schema v5) when present. Pre-v5 databases degrade
    /// silently (no annotation, no error).
    /// </summary>
    private void AnnotateExactSpanTokens(List<Dictionary<string, object?>> entries)
    {
        var spanIds = new List<string>();
        foreach (var e in entries)
        {
            if (!(string.Equals(Get(e, "_source"), "span", StringComparison.Ordinal))) continue;
            var id = Get(e, "id");
            if (!(string.Equals(id, "-", StringComparison.Ordinal))) spanIds.Add(id);
        }
        if (spanIds.Count == 0) return;

        var exact = GetExactTokensBySpanIds(spanIds);
        if (exact.Count == 0) return;

        foreach (var e in entries)
        {
            if (!(string.Equals(Get(e, "_source"), "span", StringComparison.Ordinal))) continue;
            if (exact.TryGetValue(Get(e, "id"), out var tokens))
            {
                e["_total_tokens"] = tokens.Total;
                e["_token_source"] = "exact";
                // Additive (schema v5): per-direction breakdown for the scope-trace-v1 TokenBlock.
                if (tokens.Input.HasValue) e["_input_tokens"] = tokens.Input.Value;
                if (tokens.Output.HasValue) e["_output_tokens"] = tokens.Output.Value;
            }
        }
    }

    /// <summary>
    /// Annotates span entries with wire-sourced exact token usage (wire-metering ST-03) by joining
    /// the merged wire_calls table to obs_agent_events on tool_use_id. The join key is the set
    /// intersection of <c>wire_calls.tool_use_ids</c> (a JSON array of provider-issued tool_use ids
    /// emitted in that wire call's assistant turn) with each event's <c>tool_use_id</c> (carried in
    /// the event's <c>event_data_json</c>). A matched event's tokens attribute to the span the event
    /// resolves to (write-side <c>span_id</c>, else temporal <c>_parent_span_id</c>); those are the
    /// span's DIRECT wire tokens. The rollup later sums them up the resolved-span subtree.
    /// <para>
    /// No double counting (two guards, both scoped to a single full-trace projection pass that
    /// spans all wire calls of the session):
    ///  - a tool_use_id is consumed by the FIRST wire call (in wire-row iteration order) whose array
    ///    contains it; a <c>consumedToolUseIds</c> set prevents any later wire call from re-counting
    ///    that id, so two DISTINCT wire_calls rows sharing a tool_use_id contribute that id's tokens
    ///    ONCE, not summed. (A provider issues each tool_use id in exactly one assistant turn, so on
    ///    well-formed data this never fires; it is the integrity guard for duplicate/malformed rows.)
    ///  - within one wire call, a <c>consumedWirePerSpan</c> set keyed by (span, wireId) prevents the
    ///    SAME wire call from contributing its tokens twice to one span when its array carries several
    ///    tool_use_ids that all resolve to that span.
    ///  - wire vs exact precedence is decided in the rollup/projection per task, never summed here.
    /// </para>
    /// <para>
    /// Attribution is PER-SPAN, not per-tool-call: a wire call's full token block attributes to the
    /// resolved span (the span itself accumulates direct wire tokens; the rollup later sums them up
    /// the resolved-span subtree). Splitting a multi-id wire call across its individual tool calls is
    /// deferred (ST-03b); today the whole wire block lands on the resolving span(s) under the guards
    /// above.
    /// </para>
    /// Honest-empty: when the wire_calls table is absent or carries no rows (or no tool_use_id
    /// intersects), no <c>_wire_*</c> annotation is written and behavior is identical to today
    /// (spans keep exact/estimate/unknown token sources). Never throws.
    /// </summary>
    private void AnnotateWireTokens(List<Dictionary<string, object?>> entries)
    {
        if (!HasWireCallsTable()) return;

        // Index span rows first (unconditionally): the header pass (Phase 3b) attributes leftover wire
        // rows to spans by cc_session_id / cc_agent_id even on a text-only turn that has NO joinable
        // tool_use_id, so we must NOT early-return before the header pass when spanByToolUseId is empty.
        var spanRows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!(string.Equals(Get(e, "_source"), "span", StringComparison.Ordinal))) continue;
            var id = Get(e, "id");
            if (!(string.Equals(id, "-", StringComparison.Ordinal))) spanRows[id] = e;
        }
        if (spanRows.Count == 0) return; // no spans at all -> nothing for either pass to attribute to

        // Collect the tool_use_ids actually present on this trace's events (and the resolved span
        // each one points at). Only ids present here can produce a join, so we never need to scan
        // every wire row -- we test wire-call arrays against this small set.
        var spanByToolUseId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!(string.Equals(Get(e, "_source"), "event", StringComparison.Ordinal))) continue;
            var toolUseId = ParseEventToolUseId(Get(e, "event_data_json"));
            if (string.IsNullOrEmpty(toolUseId)) continue;
            // Resolved span: write-side span_id wins, else the temporally assigned parent span.
            var span = Get(e, "span_id");
            if (string.Equals(span, "-", StringComparison.Ordinal) || span.Length == 0) span = Get(e, "_parent_span_id");
            if (string.Equals(span, "-", StringComparison.Ordinal) || span.Length == 0) continue;
            // First event wins for a given tool_use_id (provider issues each id once).
            spanByToolUseId.TryAdd(toolUseId, span);
        }

        // Per-span direct wire accumulators. Two guards, both scoped to this full-trace projection:
        //  - consumedToolUseIds: a tool_use_id is counted by the FIRST wire call (in wire-row order)
        //    whose array contains it; no later wire call re-counts that id. This is the global
        //    no-double-count guard for the duplicate-id-across-distinct-wire-rows case.
        //  - consumedWirePerSpan: within one wire call, prevents the SAME wire call from contributing
        //    its tokens twice to one span when its array carries several ids that resolve to that span.
        var acc = new Dictionary<string, WireAcc>(StringComparer.Ordinal);
        var consumedWirePerSpan = new HashSet<(string Span, string Wire)>();
        var consumedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
        // Header pass (below) only takes wire rows NOT consumed here, so each wire row is counted
        // EXACTLY ONCE across both passes. A wire row is "consumed" the moment it contributes its
        // tokens to any span via the tool_use_id join; the header pass then skips it. This is the
        // load-bearing no-double-count guard between the two passes (cron rows are all consumed here
        // first, so cron numbers stay byte-identical; the header pass only picks up leftovers).
        var consumedWireIds = new HashSet<string>(StringComparer.Ordinal);

        List<Dictionary<string, object?>> wireRows;
        try { wireRows = GetWireCalls(100000); }
        catch (SqliteException) { return; }

        foreach (var w in wireRows)
        {
            var wireId = Get(w, "id");
            var ids = ParseWireToolUseIds(Get(w, "tool_use_ids"));
            if (ids.Count == 0) continue;

            foreach (var tuid in ids)
            {
                if (!spanByToolUseId.TryGetValue(tuid, out var span)) continue;
                if (!spanRows.ContainsKey(span)) continue;
                if (!consumedToolUseIds.Add(tuid)) continue; // this id already counted by an earlier wire call
                if (!consumedWirePerSpan.Add((span, wireId))) continue; // already counted this wire call for this span

                if (!acc.TryGetValue(span, out var a)) { a = new WireAcc(); acc[span] = a; }
                a.Input += LongCol(w, "input_tokens");
                a.Output += LongCol(w, "output_tokens");
                a.CacheCreation += LongCol(w, "cache_creation_tokens");
                a.CacheRead += LongCol(w, "cache_read_tokens");
                a.Model ??= Get(w, "model") is var m && !(string.Equals(m, "-", StringComparison.Ordinal)) ? m : null;
                a.Any = true;
                // Mark this wire row consumed by the tool_use_id pass so the header pass (leftovers
                // only) never re-counts it. Additive: does not change any token already accumulated.
                consumedWireIds.Add(wireId);
            }
        }

        // ---- header-based attribution pass (T-2026-06-19-01 Phase 3b), LEFTOVERS ONLY ----
        // Attributes wire rows the tool_use_id pass did NOT consume, keyed on the documented,
        // content-free Claude Code correlation headers recorded into wire_calls (cc_session_id /
        // cc_agent_id / cc_parent_agent_id). This is a clean equality join, so it captures text-only
        // turns (no tool_use_ids) too, fixing the known CR-M1 undercount on the interactive path.
        // CRITICAL INVARIANT: it consumes ONLY rows not already in consumedWireIds, so cron numbers
        // (whose rows are all consumed above) are byte-identical to before this pass existed.
        AnnotateWireTokensByHeader(spanRows, wireRows, acc, consumedWireIds);

        foreach (var (span, a) in acc)
        {
            if (!a.Any) continue;
            var row = spanRows[span];
            row["_wire_input_tokens"] = a.Input;
            row["_wire_output_tokens"] = a.Output;
            row["_wire_cache_creation_tokens"] = a.CacheCreation;
            row["_wire_cache_read_tokens"] = a.CacheRead;
            // total mirrors the wire DTO's input+output (cache excluded from total, see WireCallDto).
            row["_wire_total_tokens"] = a.Input + a.Output;
            row["_wire_tokens"] = true;
            if (a.Model != null) row["_wire_model"] = a.Model;
        }
    }

    /// <summary>
    /// Header-based wire attribution (T-2026-06-19-01 Phase 3b), the SECOND, non-double-counting pass.
    /// Attributes wire rows the tool_use_id pass did NOT consume (those NOT in
    /// <paramref name="consumedWireIds"/>), keyed on the documented Claude Code correlation headers the
    /// proxy records into wire_calls: <c>cc_session_id</c> (per-session), <c>cc_agent_id</c>
    /// (per-subagent), and <c>cc_parent_agent_id</c> (nested spend). This is a CLEAN equality join, so
    /// it captures text-only turns (which carry no tool_use_ids) too, fixing the known CR-M1 undercount
    /// on the interactive path.
    /// <para>
    /// No double count vs the tool_use_id pass: a row is processed here ONLY if it is not already in
    /// <paramref name="consumedWireIds"/>, and it is added to that set the moment it contributes here,
    /// so each wire row is counted EXACTLY ONCE across both passes. Cron rows are all consumed by the
    /// tool_use_id pass first, so this pass takes only the leftovers and cron numbers are byte-identical.
    /// </para>
    /// <para>
    /// Binding (within the one session this trace projects):
    /// </para>
    /// <list type="bullet">
    ///   <item>Per-subagent: a leftover wire row whose <c>cc_agent_id</c> equals a span's
    ///         <c>agent_ref</c> (the host-assigned sub-agent id, the same id the wire header carries)
    ///         attributes to that span.</item>
    ///   <item>Session / parent-only: a leftover wire row with no matching sub-agent span (the parent's
    ///         own turns, including text-only turns) attributes to the session's root span (the earliest
    ///         top-level span). nested spend (<c>cc_parent_agent_id</c>) lands on the parent's span via
    ///         the same root fallback when its <c>cc_agent_id</c> has no own span.</item>
    /// </list>
    /// Honest-empty and never-throws: when no span resolves the session, or no leftover wire row binds,
    /// no annotation is written and behavior is identical to the tool_use_id-only pass. The accumulators
    /// it writes into are the SAME ones the projection reads, so the rollup, the Cost Ledger, and the
    /// trace Accounting total all derive from one canonical source (no divergent total).
    /// </summary>
    private void AnnotateWireTokensByHeader(
        Dictionary<string, Dictionary<string, object?>> spanRows,
        List<Dictionary<string, object?>> wireRows,
        Dictionary<string, WireAcc> acc,
        HashSet<string> consumedWireIds)
    {
        if (spanRows.Count == 0) return;

        // Fast exit: if no UNCONSUMED wire row carries a cc_session_id header, there is nothing for the
        // header pass to bind. This keeps the steady state (cron-only / pre-header dbs) free of the
        // extra bridge/schema/agent_ref queries below -- they run only when interactive header rows
        // actually exist as leftovers.
        var hasLeftoverHeaderRow = false;
        foreach (var w in wireRows)
        {
            if (consumedWireIds.Contains(Get(w, "id"))) continue;
            var cc = Get(w, "cc_session_id");
            if (!string.Equals(cc, "-", StringComparison.Ordinal) && cc.Length > 0) { hasLeftoverHeaderRow = true; break; }
        }
        if (!hasLeftoverHeaderRow) return;

        // The merged trace projects exactly one Scope session; take its session key from any span row.
        string? scopeSessionKey = null;
        foreach (var (_, r) in spanRows)
        {
            var sk = Get(r, "session_key");
            if (!string.Equals(sk, "-", StringComparison.Ordinal) && sk.Length > 0) { scopeSessionKey = sk; break; }
        }
        if (scopeSessionKey == null) return;

        // Candidate CC session ids that bind to this Scope session:
        //  - the D3 bridge value (obs_agent_sessions.cc_session_id), set by the hook for a Scope-launched
        //    cli-{uuid7} session whose CC session id is distinct from the Scope key; and
        //  - the Scope session key itself, which for a host-provided session IS Claude Code's session id.
        // Either way, a leftover wire row whose cc_session_id is in this set belongs to this session.
        //
        // Per-generation correctness (T-2026-06-19-03 Wave 2 / ST-06): with session generations, a trace
        // projects exactly ONE generation, so scopeSessionKey here is that generation's EFFECTIVE key
        // (cli-x, or cli-x#g{n}). Each generation's obs_agent_sessions row carries its OWN cc_session_id
        // (UpsertAgentSession stamps the per-event CC id keyed by the effective key), so
        // ResolveCcSessionId(effectiveKey) returns the cc id of THIS generation, not g0. A leftover wire
        // row therefore attributes to the generation whose cc_session_id matches its wire header, not
        // dumped onto the first generation. ResolveCcSessionId self-ensures the obs schema (the v12
        // reader-gap guard), so this SELECT never 500s on a lazily-migrated live db.
        var ccSessionIds = new HashSet<string>(StringComparer.Ordinal) { scopeSessionKey };
        var bridged = ResolveCcSessionId(scopeSessionKey);
        if (!string.IsNullOrEmpty(bridged)) ccSessionIds.Add(bridged);
        // conversation_id (schema v16): transcript-source wire rows key on the transcript stem
        // (= conversation_id), which equals cc_session_id on the first-ever session but diverges
        // on resume (the transcript file is continuous; the host session_id is not). Adding it as
        // a third candidate ensures transcript-cost rows still attribute on resume.
        var convId = ResolveConversationId(scopeSessionKey);
        if (!string.IsNullOrEmpty(convId)) ccSessionIds.Add(convId);

        // Map each span's agent_ref (host sub-agent id) to its span id for per-subagent binding.
        // agent_ref is a schema-v5 column; read it via a dedicated tolerant query (NOT in the main
        // GetAgentSpans SELECT) so a pre-v5 db degrades to "no per-subagent binding" instead of failing
        // the whole span read. Only spans present in this trace (spanRows) are eligible targets.
        var spanByAgentRef = GetSpanAgentRefs(scopeSessionKey);
        spanByAgentRef = spanByAgentRef
            .Where(kv => spanRows.ContainsKey(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // Session root span: the earliest top-level span (parent_span_id absent or not a known span).
        // spanRows enumeration order is not guaranteed, so pick the one with the smallest started_at.
        string? rootSpanId = null;
        string? rootStarted = null;
        foreach (var (id, r) in spanRows)
        {
            var parent = Get(r, "parent_span_id");
            var hasKnownParent = !string.Equals(parent, "-", StringComparison.Ordinal) && parent.Length > 0 && spanRows.ContainsKey(parent);
            if (hasKnownParent) continue; // not top-level
            var started = Get(r, "started_at");
            if (rootStarted == null || string.Compare(started, rootStarted, StringComparison.Ordinal) < 0)
            {
                rootStarted = started;
                rootSpanId = id;
            }
        }
        if (rootSpanId == null) return; // no top-level span to anchor session/parent-only spend

        foreach (var w in wireRows)
        {
            var wireId = Get(w, "id");
            if (consumedWireIds.Contains(wireId)) continue; // leftovers only -> no double count

            var ccSession = Get(w, "cc_session_id");
            if (string.Equals(ccSession, "-", StringComparison.Ordinal) || ccSession.Length == 0) continue; // header-less row
            if (!ccSessionIds.Contains(ccSession)) continue; // belongs to a different session

            // Per-subagent binding: cc_agent_id -> a span's agent_ref. Otherwise the parent's own spend
            // (including text-only turns) anchors on the session root span.
            var ccAgent = Get(w, "cc_agent_id");
            string targetSpan;
            if (!string.Equals(ccAgent, "-", StringComparison.Ordinal) && ccAgent.Length > 0
                && spanByAgentRef.TryGetValue(ccAgent, out var subSpan))
            {
                targetSpan = subSpan;
            }
            else
            {
                targetSpan = rootSpanId;
            }

            if (!acc.TryGetValue(targetSpan, out var a)) { a = new WireAcc(); acc[targetSpan] = a; }
            a.Input += LongCol(w, "input_tokens");
            a.Output += LongCol(w, "output_tokens");
            a.CacheCreation += LongCol(w, "cache_creation_tokens");
            a.CacheRead += LongCol(w, "cache_read_tokens");
            a.Model ??= Get(w, "model") is var m && !(string.Equals(m, "-", StringComparison.Ordinal)) ? m : null;
            a.Any = true;
            consumedWireIds.Add(wireId); // counted exactly once across both passes
        }
    }

    /// <summary>
    /// Resolves the Claude Code session id bound to a Scope session key via the D3 bridge
    /// (<c>obs_agent_sessions.cc_session_id</c>, schema v11). Returns null when the column is absent
    /// (pre-v11 db: read-best-effort, no throw) or no binding exists. Self-ensures the obs schema once
    /// so a live db bumped to v11 on the write path but read on an older connection does not fail the
    /// SELECT (the lazy-migration reader-gap precedent from Phase 1).
    /// </summary>
    private string? ResolveCcSessionId(string scopeSessionKey)
    {
        try { new ObservabilitySchemaManager(connection).EnsureSchema(); }
        catch (SqliteException) { /* best-effort; the SELECT below still degrades safely */ }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT cc_session_id FROM obs_agent_sessions WHERE session_key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", scopeSessionKey);
        try
        {
            var result = cmd.ExecuteScalarRetrying();
            return result is string s && s.Length > 0 ? s : null;
        }
        catch (SqliteException) { return null; /* pre-v11 schema: no cc_session_id column */ }
    }

    /// <summary>
    /// Reads the resume-stable <c>conversation_id</c> for a given Scope session key from
    /// <c>obs_agent_sessions.conversation_id</c> (schema v16). Returns null when the column is
    /// absent (pre-v16 db) or no binding exists. Tolerant: a SqliteException on missing column
    /// degrades to null; observability must not crash callers.
    /// </summary>
    /// <remarks>
    /// Does NOT call EnsureSchema itself; safe only because <see cref="ResolveCcSessionId"/> (which
    /// does call EnsureSchema) is always invoked immediately before this in AnnotateWireTokensByHeader.
    /// </remarks>
    private string? ResolveConversationId(string scopeSessionKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT conversation_id FROM obs_agent_sessions WHERE session_key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", scopeSessionKey);
        try
        {
            var result = cmd.ExecuteScalarRetrying();
            return result is string s && s.Length > 0 ? s : null;
        }
        catch (SqliteException) { return null; /* pre-v16 schema: no conversation_id column */ }
    }

    /// <summary>
    /// Returns a map of <c>agent_ref</c> (the host-assigned sub-agent id, the same value the wire
    /// header X-Claude-Code-Agent-Id carries) to the span id for a session's spans. Used by the
    /// header-based wire attribution pass to bind a leftover wire row's cc_agent_id to the right
    /// sub-agent span. <c>agent_ref</c> is a schema-v5 column read in its own tolerant query (NOT in
    /// the main GetAgentSpans SELECT) so a pre-v5 db degrades to an empty map (no per-subagent binding)
    /// instead of throwing. First span wins for a given agent_ref (a host id maps to one span).
    /// </summary>
    private Dictionary<string, string> GetSpanAgentRefs(string sessionKey)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_ref FROM obs_agent_spans
            WHERE session_key = $key AND agent_ref IS NOT NULL
            ORDER BY started_at ASC;
            """;
        cmd.Parameters.AddWithValue("$key", sessionKey);
        try
        {
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;
                var aref = reader.GetString(1);
                if (aref.Length == 0) continue;
                result.TryAdd(aref, reader.GetString(0));
            }
        }
        catch (SqliteException) { /* pre-v5 schema: no agent_ref column -> no per-subagent binding */ }
        return result;
    }

    private sealed class WireAcc
    {
        public long Input, Output, CacheCreation, CacheRead;
        public string? Model;
        public bool Any;
    }

    private static long LongCol(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return 0;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
            _ => 0,
        };
    }

    /// <summary>
    /// Extracts <c>tool_use_id</c> from a raw <c>event_data_json</c> string.
    /// Returns null when the JSON is absent, unparseable, or has no <c>tool_use_id</c> string property.
    /// Exposed as internal so <see cref="ObservabilityDb"/> can reuse it at insert time without
    /// re-inventing the JSON parse (ST-01 requirement: prefer reuse over duplication).
    /// </summary>
    internal static string? ParseEventToolUseId(string? raw)
    {
        if (string.Equals(raw, "-", StringComparison.Ordinal) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tool_use_id", out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<string> ParseWireToolUseIds(string raw)
    {
        if (string.Equals(raw, "-", StringComparison.Ordinal) || string.IsNullOrEmpty(raw)) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) result.Add(el.GetString()!);
            return result;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Returns a map of span id -> exact host-reported token usage (total plus optional input/output
    /// breakdown, schema v5) for spans that have it. Guarded for pre-v5 databases where the columns
    /// do not exist (returns whatever was read before the failing column).
    /// </summary>
    private Dictionary<string, (long Total, long? Input, long? Output)> GetExactTokensBySpanIds(List<string> spanIds)
    {
        var result = new Dictionary<string, (long, long?, long?)>(StringComparer.Ordinal);
        const int batchSize = 50;
        for (var i = 0; i < spanIds.Count; i += batchSize)
        {
            var batch = spanIds.GetRange(i, Math.Min(batchSize, spanIds.Count - i));
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>();
            for (var j = 0; j < batch.Count; j++)
            {
                var param = $"$s{j}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, batch[j]);
            }

            cmd.CommandText = $"""
                SELECT id, total_tokens, input_tokens, output_tokens FROM obs_agent_spans
                WHERE total_tokens IS NOT NULL AND id IN ({string.Join(", ", placeholders)});
                """;

            try
            {
                using var reader = cmd.ExecuteReaderRetrying();
                while (reader.Read())
                {
                    var input = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                    var output = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                    result[reader.GetString(0)] = (reader.GetInt64(1), input, output);
                }
            }
            catch (SqliteException) { return result; /* pre-v5 schema: missing token columns */ }
        }
        return result;
    }

    /// <summary>
    /// Returns a map of request_id -> SUM(total_cost_usd) for the given request IDs.
    /// </summary>
    private Dictionary<string, double> GetCostTotalsByRequestIds(List<string> requestIds)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (requestIds.Count == 0) return result;

        // Batch in groups to avoid SQL parameter limits
        const int batchSize = 100;
        for (var i = 0; i < requestIds.Count; i += batchSize)
        {
            var batch = requestIds.GetRange(i, Math.Min(batchSize, requestIds.Count - i));
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>();
            for (var j = 0; j < batch.Count; j++)
            {
                var param = $"$r{j}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, batch[j]);
            }

            cmd.CommandText = $"""
                SELECT request_id, SUM(total_cost_usd) AS total
                FROM obs_cost_estimates
                WHERE request_id IN ({string.Join(", ", placeholders)})
                GROUP BY request_id;
                """;

            try
            {
                using var reader = cmd.ExecuteReaderRetrying();
                while (reader.Read())
                {
                    var reqId = reader.GetString(0);
                    var total = reader.GetDouble(1);
                    result[reqId] = total;
                }
            }
            catch (SqliteException) { /* table may not exist */ }
        }

        return result;
    }

    private static string Get(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var val) ? val?.ToString() ?? "-" : "-";

    /// <summary>
    /// Returns true when the wire_calls table (wire-metering ST-01) exists in the database.
    /// The table is created lazily by the metering proxy on its first write, so a workspace that
    /// has never run the proxy has no such table; the trace API serves an honest empty wire section.
    /// </summary>
    public bool HasWireCallsTable()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='wire_calls';";
        bool exists;
        try { exists = Convert.ToInt32(cmd.ExecuteScalarRetrying(), CultureInfo.InvariantCulture) > 0; }
        catch (SqliteException) { return false; }

        // Lazy-migration reader gap (T-2026-06-19-01): wire_calls is migrated by the WRITE path
        // (WireCallDb / the metering proxy). This is a READ-only connection; a live v1-shaped db
        // that has not been written since the schema bumped to v2 lacks the cc_* columns, so the
        // GetWireCalls SELECT below would fail "no such column" and silently degrade to empty.
        // When the table exists, self-ensure the wire schema is current on this connection (the
        // same self-ensure pattern the Cost Ledger reader uses for the obs_* chain). EnsureSchema
        // is additive + idempotent; it never throws into the caller.
        if (exists)
        {
            try { new WireCallSchemaManager(connection).EnsureSchema(); }
            catch (SqliteException) { /* read path stays best-effort; SELECT still degrades safely */ }
        }

        return exists;
    }

    /// <summary>
    /// Queries wire_calls rows (wire-metering ST-01), newest first. Returns an empty list when the
    /// table does not exist or any storage error occurs (graceful degradation, never fabricated).
    /// Column set mirrors the merged WireCallSchemaManager DDL field-for-field.
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only wire calls
    /// whose started_at is on or after this value are returned (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted; this is a read filter only.
    /// </summary>
    public List<Dictionary<string, object?>> GetWireCalls(int limit, string? since = null)
    {
        if (!HasWireCallsTable()) return [];
        using var cmd = connection.CreateCommand();
        var where = since != null ? "WHERE started_at >= $since" : "";
        if (since != null) cmd.Parameters.AddWithValue("$since", since);
        cmd.CommandText = $"""
            SELECT id, started_at, ttft_ms, total_ms, endpoint, status, stream,
                   model, message_id, stop_reason,
                   input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens,
                   tool_use_ids, system_hash, first_user_hash, message_count,
                   request_bytes, response_bytes,
                   cc_session_id, cc_agent_id, cc_parent_agent_id, source
            FROM wire_calls
            {where}
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        try { return ReadRows(cmd); }
        catch (SqliteException) { return []; }
    }

    /// <summary>
    /// Returns the distinct set of provider-issued <c>tool_use_id</c> values carried by the
    /// <c>obs_agent_events</c> rows that belong to the given dispatch. A dispatch owns an event when
    /// the event's <c>event_data_json</c> carries <c>$.dispatch_id == dispatchId</c> (the same dispatch
    /// correlation key <see cref="GetToolCallEvents"/> filters on). The returned ids are the join key
    /// the wire-cost rollup intersects against <c>wire_calls.tool_use_ids</c> to attribute exact wire
    /// token usage to the dispatch. Never throws: any storage error yields an empty set.
    /// <para>
    /// This is the dispatch-scoped counterpart to the per-span tool_use_id collection in
    /// <see cref="AnnotateWireTokens"/>; it deliberately does NOT resolve spans (the rollup needs a
    /// dispatch total, not per-span attribution), so it lives separately to avoid perturbing trace
    /// projection behavior.
    /// </para>
    /// </summary>
    public HashSet<string> GetDispatchToolUseIds(string dispatchId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dispatchId)) return result;

        using var cmd = connection.CreateCommand();
        // Guard json_extract with json_valid: some event_data_json values are plain text, not JSON
        // (notably user_submit rows store the raw human prompt verbatim). SQLite's json_extract raises
        // "malformed JSON" on the first non-JSON row and aborts the whole scan, which would silently
        // drop every tool_use_id for the dispatch and zero its wire cost. Filtering to valid-JSON rows
        // first makes the scan tolerant of the privileged user_submit content rows.
        cmd.CommandText = """
            SELECT event_data_json FROM obs_agent_events
            WHERE json_valid(event_data_json)
              AND json_extract(event_data_json, '$.dispatch_id') = $dispatchId;
            """;
        cmd.Parameters.AddWithValue("$dispatchId", dispatchId);

        try
        {
            using var reader = cmd.ExecuteReaderRetrying();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var toolUseId = ParseEventToolUseId(reader.GetString(0));
                if (!string.IsNullOrEmpty(toolUseId)) result.Add(toolUseId);
            }
        }
        catch (SqliteException) { return result; }

        return result;
    }

    /// <summary>
    /// Exports all trace data for a run, dispatch, or single request, including stage details per request.
    /// Resolution order: session key -> dispatch ID (with prefix resolution) -> request ID -> error.
    /// </summary>
    public Dictionary<string, object?> GetExportData(string identifier)
    {
        // Try as session key first
        var requests = GetTraceEntries(new AuditFilters(RunKey: identifier, FullPayloads: true, Limit: 10000));
        var type = "run";
        AuditFilters? spanEventFilters = null;
        if (requests.Count > 0)
        {
            spanEventFilters = new AuditFilters(RunKey: identifier, FullPayloads: true, Limit: 10000);
        }
        if (requests.Count == 0)
        {
            // Resolve dispatch prefix before querying
            var resolvedId = ResolveDispatchPrefix(identifier);
            if (resolvedId != null)
            {
                requests = GetTraceEntries(new AuditFilters(DispatchIdFilter: resolvedId, FullPayloads: true, Limit: 10000));
                identifier = resolvedId;
            }
            type = "dispatch";
            if (requests.Count > 0)
                spanEventFilters = new AuditFilters(DispatchIdFilter: identifier, FullPayloads: true, Limit: 10000);
        }
        if (requests.Count == 0)
        {
            requests = GetTraceEntries(new AuditFilters(RequestId: identifier, FullPayloads: true, Limit: 1));
            type = "request";
            if (requests.Count > 0)
                spanEventFilters = new AuditFilters(RequestId: identifier, FullPayloads: true, Limit: 10000);
        }
        if (requests.Count == 0)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["error"] = "No data found for identifier: " + identifier };

        var enriched = new List<Dictionary<string, object?>>();
        foreach (var req in requests)
        {
            var reqId = req.TryGetValue("id", out var idVal) ? idVal?.ToString() : null;
            if (reqId != null)
            {
                req["stages"] = GetStagesByRequestId(reqId);
            }
            enriched.Add(req);
        }

        var spans = spanEventFilters != null ? GetAgentSpans(spanEventFilters) : [];
        var events = spanEventFilters != null ? GetToolCallEvents(spanEventFilters) : [];

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["id"] = identifier,
            ["requests"] = enriched,
            ["spans"] = spans,
            ["events"] = events
        };
    }

    /// <summary>
    /// Returns session graph data for the HTML session graph visualization.
    /// Each session node includes: session_key, agent_name (resolved from most recent span or agent_type),
    /// parent_session_key, request_count, first_seen_at, last_seen_at, total_cost_usd, _depth.
    /// Resolves agent_name from obs_agent_spans (most recent agent_name) falling back to agent_type:agent_subtype.
    ///
    /// <paramref name="since"/> is an optional ISO-8601 UTC cutoff; when non-null only sessions
    /// whose last_seen_at is on or after this value are included (T-2026-06-13-33 retention gate).
    /// Rows older than the cutoff are NOT deleted; this is a read filter only.
    /// </summary>
    public List<Dictionary<string, object?>> GetSessionGraphData(string sessionKey, string? since = null)
    {
        var tree = GetSessionTree(sessionKey, since);
        if (tree.Count == 0) return [];

        // Resolve friendly names from agent spans
        var sessionKeys = tree.Select(s => Get(s, "session_key")).Where(k => !(string.Equals(k, "-", StringComparison.Ordinal))).ToList();
        var nameMap = ResolveAgentNames(sessionKeys);

        // Compute costs per session
        var costMap = ComputeSessionCosts(sessionKeys);

        foreach (var session in tree)
        {
            var key = Get(session, "session_key");
            session["agent_name"] = nameMap.TryGetValue(key, out var name) ? name : FormatSessionName(session);
            session["total_cost_usd"] = costMap.TryGetValue(key, out var cost) ? cost : 0.0;
        }

        return tree;
    }

    /// <summary>
    /// Enriches a session graph with agent span nodes when session-level hierarchy is absent.
    /// For leaf sessions (no child sessions), queries agent spans and adds them as child nodes
    /// in the graph, splitting trace entries accordingly. This provides a dispatch tree view
    /// even when parent_session_key is not populated (e.g., PostToolUse hook integration).
    /// </summary>
    public void EnrichGraphWithSpans(
        List<Dictionary<string, object?>> graph,
        Dictionary<string, List<Dictionary<string, object?>>> perSessionTraces)
    {
        // Find sessions that already have children (skip enrichment for those)
        var keysWithChildren = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph)
        {
            var pk = Get(node, "parent_session_key");
            if (!(string.Equals(pk, "-", StringComparison.Ordinal))) keysWithChildren.Add(pk);
        }

        var additions = new List<Dictionary<string, object?>>();

        foreach (var session in graph.ToList())
        {
            var sessionKey = Get(session, "session_key");
            if (keysWithChildren.Contains(sessionKey)) continue;
            if (sessionKey.StartsWith("span:", StringComparison.Ordinal)) continue;

            var sessionDepth = session.TryGetValue("_depth", out var d) && d is int di ? di : 0;

            List<Dictionary<string, object?>> spans;
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT id, agent_type, agent_name, parent_span_id, started_at, completed_at, status
                    FROM obs_agent_spans
                    WHERE session_key = $key
                    ORDER BY started_at;
                    """;
                cmd.Parameters.AddWithValue("$key", sessionKey);
                spans = ReadRows(cmd);
            }
            catch (SqliteException) { continue; }

            if (spans.Count == 0) continue;

            perSessionTraces.TryGetValue(sessionKey, out var sessionEntries);
            sessionEntries ??= [];
            var assignedIndices = new HashSet<int>();

            foreach (var span in spans)
            {
                var spanId = Get(span, "id");
                if (string.Equals(spanId, "-", StringComparison.Ordinal)) continue;

                var parentSpanId = Get(span, "parent_span_id");
                var agentName = Get(span, "agent_name");
                var pseudoKey = $"span:{spanId}";

                var parentKey = !(string.Equals(parentSpanId, "-", StringComparison.Ordinal)) ? $"span:{parentSpanId}" : sessionKey;

                var parentNode = string.Equals(parentKey, sessionKey, StringComparison.Ordinal)
                    ? session
                    : additions.Find(a => string.Equals(Get(a, "session_key"), parentKey, StringComparison.Ordinal));
                var parentDepth = parentNode?.TryGetValue("_depth", out var pd) == true && pd is int pdi
                    ? pdi : sessionDepth;

                var spanEntries = new List<Dictionary<string, object?>>();
                for (var i = 0; i < sessionEntries.Count; i++)
                {
                    if (assignedIndices.Contains(i)) continue;
                    var entrySpanId = sessionEntries[i].TryGetValue("span_id", out var sid)
                        ? sid?.ToString() : null;
                    if (string.Equals(entrySpanId, spanId, StringComparison.Ordinal))
                    {
                        spanEntries.Add(sessionEntries[i]);
                        assignedIndices.Add(i);
                    }
                }

                additions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["session_key"] = pseudoKey,
                    ["agent_name"] = !(string.Equals(agentName, "-", StringComparison.Ordinal)) ? agentName : "Agent",
                    ["parent_session_key"] = parentKey,
                    ["_depth"] = parentDepth + 1,
                    ["request_count"] = spanEntries.Count.ToString(CultureInfo.InvariantCulture),
                    ["total_cost_usd"] = "0",
                });

                if (spanEntries.Count > 0)
                    perSessionTraces[pseudoKey] = spanEntries;
            }

            if (assignedIndices.Count > 0)
            {
                var remaining = new List<Dictionary<string, object?>>();
                for (var i = 0; i < sessionEntries.Count; i++)
                {
                    if (!assignedIndices.Contains(i))
                        remaining.Add(sessionEntries[i]);
                }
                perSessionTraces[sessionKey] = remaining;
            }
        }

        graph.AddRange(additions);
    }

    /// <summary>
    /// Resolves agent names from the most recent agent span for each session key.
    /// Falls back to empty string if no spans exist.
    /// </summary>
    private Dictionary<string, string> ResolveAgentNames(List<string> sessionKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sessionKeys.Count == 0) return result;

        const int batchSize = 50;
        for (var i = 0; i < sessionKeys.Count; i += batchSize)
        {
            var batch = sessionKeys.GetRange(i, Math.Min(batchSize, sessionKeys.Count - i));
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>();
            for (var j = 0; j < batch.Count; j++)
            {
                var param = $"$k{j}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, batch[j]);
            }

            cmd.CommandText = $"""
                SELECT session_key, agent_name
                FROM obs_agent_spans
                WHERE session_key IN ({string.Join(", ", placeholders)})
                ORDER BY started_at DESC;
                """;

            try
            {
                using var reader = cmd.ExecuteReaderRetrying();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var agentName = reader.GetString(1);
                    // First row per session_key is the most recent (DESC order)
                    if (!result.ContainsKey(key))
                        result[key] = agentName;
                }
            }
            catch (SqliteException) { /* table may not exist */ }
        }

        return result;
    }

    /// <summary>
    /// Computes total cost per session key by summing cost estimates for all requests in each session.
    /// </summary>
    private Dictionary<string, double> ComputeSessionCosts(List<string> sessionKeys)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (sessionKeys.Count == 0) return result;

        const int batchSize = 50;
        for (var i = 0; i < sessionKeys.Count; i += batchSize)
        {
            var batch = sessionKeys.GetRange(i, Math.Min(batchSize, sessionKeys.Count - i));
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>();
            for (var j = 0; j < batch.Count; j++)
            {
                var param = $"$k{j}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, batch[j]);
            }

            cmd.CommandText = $"""
                SELECT r.agent_session_key, SUM(c.total_cost_usd)
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE r.agent_session_key IN ({string.Join(", ", placeholders)})
                GROUP BY r.agent_session_key;
                """;

            try
            {
                using var reader = cmd.ExecuteReaderRetrying();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var total = reader.GetDouble(1);
                    result[key] = total;
                }
            }
            catch (SqliteException) { /* table may not exist */ }
        }

        return result;
    }

    private static string FormatSessionName(Dictionary<string, object?> session)
    {
        var agentType = Get(session, "agent_type");
        var agentSubtype = Get(session, "agent_subtype");
        if (!(string.Equals(agentType, "-", StringComparison.Ordinal)) && !(string.Equals(agentSubtype, "-", StringComparison.Ordinal)))
            return $"{agentType}:{agentSubtype}";
        if (!(string.Equals(agentType, "-", StringComparison.Ordinal))) return agentType;
        return Get(session, "session_key");
    }

    /// <summary>
    /// Reads all rows from a command into a list of dictionaries keyed by column name.
    /// </summary>
    private static List<Dictionary<string, object?>> ReadRows(SqliteCommand cmd)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReaderRetrying();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}

#pragma warning restore MA0016
