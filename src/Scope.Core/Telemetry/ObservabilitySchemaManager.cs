namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;

/// <summary>
/// Creates the obs_* observability tables in .scope/telemetry.db.
/// Coexists with the legacy retrieval_requests/retrieval_results tables
/// managed by <see cref="TelemetrySchemaManager"/>.
/// </summary>
public class ObservabilitySchemaManager(SqliteConnection connection)
{
    /// <summary>
    /// Ensures all observability tables and indexes exist.
    /// Safe to call multiple times (CREATE TABLE IF NOT EXISTS).
    /// </summary>
    public void EnsureSchema()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_schema_versions (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                description TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_requests (
                id                TEXT PRIMARY KEY,
                parent_request_id TEXT,
                caller_surface    TEXT NOT NULL,
                operation         TEXT NOT NULL,
                agent_session_key TEXT,
                query_text        TEXT,
                parameters_json   TEXT,
                status            TEXT NOT NULL DEFAULT 'started',
                exit_code         INTEGER,
                error_message     TEXT,
                total_elapsed_ms  INTEGER,
                created_at        TEXT NOT NULL,
                completed_at      TEXT
            );

            CREATE TABLE IF NOT EXISTS obs_request_stages (
                id            TEXT PRIMARY KEY,
                request_id    TEXT NOT NULL REFERENCES obs_requests(id),
                stage_name    TEXT NOT NULL,
                stage_order   INTEGER NOT NULL,
                elapsed_ms    INTEGER NOT NULL,
                metadata_json TEXT,
                created_at    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_retrieval_results (
                id              TEXT PRIMARY KEY,
                request_id      TEXT NOT NULL REFERENCES obs_requests(id),
                document_key    TEXT NOT NULL,
                rank            INTEGER NOT NULL,
                score           REAL NOT NULL,
                provider_source TEXT NOT NULL,
                is_exact_match  INTEGER NOT NULL DEFAULT 0,
                boosts_applied  TEXT NOT NULL DEFAULT '',
                created_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_packing_metrics (
                id                   TEXT PRIMARY KEY,
                request_id           TEXT NOT NULL REFERENCES obs_requests(id),
                target_fqn           TEXT NOT NULL,
                budget_requested     INTEGER NOT NULL,
                budget_used          INTEGER NOT NULL,
                budget_remaining     INTEGER NOT NULL,
                symbols_considered   INTEGER NOT NULL,
                symbols_included     INTEGER NOT NULL,
                symbols_rejected     INTEGER NOT NULL,
                compression_ct       INTEGER NOT NULL,
                full_body_ct         INTEGER NOT NULL,
                signature_only_ct    INTEGER NOT NULL,
                call_site_ct         INTEGER NOT NULL,
                dedup_removed_ct     INTEGER NOT NULL,
                free_symbol_ct       INTEGER NOT NULL,
                test_callers_capped  INTEGER NOT NULL DEFAULT 0,
                target_token_count   INTEGER NOT NULL,
                context_token_count  INTEGER NOT NULL,
                created_at           TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_cost_estimates (
                id                      TEXT PRIMARY KEY,
                request_id              TEXT NOT NULL REFERENCES obs_requests(id),
                model_id                TEXT NOT NULL,
                provider                TEXT NOT NULL,
                input_tokens            INTEGER NOT NULL,
                estimated_output_tokens INTEGER NOT NULL,
                input_cost_usd          REAL NOT NULL,
                output_cost_usd         REAL NOT NULL,
                total_cost_usd          REAL NOT NULL,
                created_at              TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_agent_sessions (
                session_key   TEXT PRIMARY KEY,
                agent_type    TEXT NOT NULL,
                agent_subtype TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL,
                request_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS obs_agent_events (
                id              TEXT PRIMARY KEY,
                session_key     TEXT NOT NULL REFERENCES obs_agent_sessions(session_key),
                request_id      TEXT REFERENCES obs_requests(id),
                event_type      TEXT NOT NULL,
                event_data_json TEXT,
                created_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS obs_payloads (
                id           TEXT PRIMARY KEY,
                request_id   TEXT NOT NULL REFERENCES obs_requests(id),
                direction    TEXT NOT NULL CHECK(direction IN ('inbound','outbound')),
                content_type TEXT NOT NULL,
                payload_text TEXT NOT NULL,
                token_count  INTEGER,
                created_at   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_obsreq_surface   ON obs_requests(caller_surface);
            CREATE INDEX IF NOT EXISTS idx_obsreq_operation  ON obs_requests(operation);
            CREATE INDEX IF NOT EXISTS idx_obsreq_agent      ON obs_requests(agent_session_key);
            CREATE INDEX IF NOT EXISTS idx_obsreq_created    ON obs_requests(created_at);
            CREATE INDEX IF NOT EXISTS idx_obsreq_parent     ON obs_requests(parent_request_id);
            CREATE INDEX IF NOT EXISTS idx_obsstage_request  ON obs_request_stages(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsret_request    ON obs_retrieval_results(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsret_dockey     ON obs_retrieval_results(document_key);
            CREATE INDEX IF NOT EXISTS idx_obspack_request   ON obs_packing_metrics(request_id);
            CREATE INDEX IF NOT EXISTS idx_obscost_request   ON obs_cost_estimates(request_id);
            CREATE INDEX IF NOT EXISTS idx_obscost_created   ON obs_cost_estimates(created_at);
            CREATE INDEX IF NOT EXISTS idx_obsagent_session  ON obs_agent_events(session_key);
            CREATE INDEX IF NOT EXISTS idx_obsagent_request  ON obs_agent_events(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsagent_type     ON obs_agent_events(event_type);
            CREATE INDEX IF NOT EXISTS idx_obspay_request    ON obs_payloads(request_id);
            CREATE INDEX IF NOT EXISTS idx_obspay_direction  ON obs_payloads(direction);
            """;
        cmd.ExecuteNonQueryRetrying();

        InsertSchemaVersion(1, "Initial observability schema with obs_* tables");

        MigrateToV2();
        MigrateToV3();
    }

    private void MigrateToV2()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_dispatches (
                id           TEXT PRIMARY KEY,
                session_key  TEXT,
                label        TEXT,
                created_at   TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_obsdisp_session ON obs_dispatches(session_key);
            """;
        cmd.ExecuteNonQueryRetrying();

        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_requests ADD COLUMN dispatch_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsreq_dispatch ON obs_requests(dispatch_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(2, "Dispatch correlation");
    }

    private void MigrateToV3()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_agent_spans (
                id              TEXT PRIMARY KEY,
                session_key     TEXT NOT NULL,
                dispatch_id     TEXT,
                parent_span_id  TEXT,
                agent_type      TEXT NOT NULL,
                agent_name      TEXT NOT NULL,
                started_at      TEXT NOT NULL,
                completed_at    TEXT,
                input_summary   TEXT,
                output_summary  TEXT,
                status          TEXT NOT NULL DEFAULT 'active'
            );
            CREATE INDEX IF NOT EXISTS idx_obsspan_session ON obs_agent_spans(session_key);
            CREATE INDEX IF NOT EXISTS idx_obsspan_dispatch ON obs_agent_spans(dispatch_id);
            CREATE INDEX IF NOT EXISTS idx_obsspan_parent ON obs_agent_spans(parent_span_id);
            """;
        cmd.ExecuteNonQueryRetrying();

        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_events ADD COLUMN span_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsagent_span ON obs_agent_events(span_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(3, "Agent span hierarchy");
        MigrateToV4();
    }

    private void MigrateToV4()
    {
        // Add parent_session_key to obs_agent_sessions for session hierarchy
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN parent_session_key TEXT;";
            cmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_parent ON obs_agent_sessions(parent_session_key);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(4, "Agent session continuity");
        MigrateToV5();
    }

    private void MigrateToV5()
    {
        // Additive columns on obs_agent_spans: agent_ref stores the host-assigned agent id
        // of the spawned agent (from the Agent tool result) so subsequent payloads carrying
        // that id as agent_id can be linked back to this span; the token columns store exact
        // usage from synchronous Agent completions for span-level attribution.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_spans ADD COLUMN agent_ref TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN total_tokens INTEGER;",
            "ALTER TABLE obs_agent_spans ADD COLUMN input_tokens INTEGER;",
            "ALTER TABLE obs_agent_spans ADD COLUMN output_tokens INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsspan_agentref ON obs_agent_spans(session_key, agent_ref);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(5, "Span agent linkage (agent_ref) and exact token usage");
        MigrateToV6();
    }

    private void MigrateToV6()
    {
        // Add pinned-cost columns to obs_agent_spans for price-immutability.
        // The span is the cron rollup carrier: cron sessions have NO obs_requests row
        // (tool_call events carry request_id=NULL), so pinning on obs_cost_estimates
        // (which requires a NOT NULL request_id FK) cannot serve the cron rollup.
        // Pinning here lets TraceRollup.ResolveTaskCost read the stored snapshot and
        // emit pinned=true instead of re-deriving from the live pricing table.
        // pinned_breakdown_json stores the serialized List<CostBreakdownComponent> so the
        // breakdown can be reconstructed exactly without re-reading rates.
        // Forward-only: nullable, no backfill. Legacy spans stay re-derived (pinned=false).
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_cost_usd REAL;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_model_id TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_prices_as_of TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_breakdown_json TEXT;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }
        InsertSchemaVersion(6, "Cost pinning: price snapshot at capture in obs_agent_spans");
        MigrateToV7();
    }

    private void MigrateToV7()
    {
        // A5 transparency (T-2026-06-15-17): persist how the session key was obtained so the
        // origin is shown, never implied. Values: 'scope-launched' (tier 1 SCOPE_SESSION_KEY),
        // 'host-provided' (tier 2 best-effort host session id), 'unattributed' (tier 3 per-day
        // bucket). Forward-only, nullable; legacy rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN origin TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(7, "Session origin label (scope-launched/host-provided/unattributed)");
        MigrateToV8();
    }

    private void MigrateToV8()
    {
        // Soft-delete support (T-2026-06-15-16/ST-01): persist when a session was soft-deleted so it
        // can be hidden by default, surfaced in a trash view, and restored. ISO-8601 UTC string when
        // soft-deleted, NULL otherwise. Reversible by design: only this flag is touched, never the
        // session's events/spans/requests (NO cascade). Forward-only, nullable; legacy rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN deleted_at TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(8, "Session soft-delete flag (deleted_at) on obs_agent_sessions");
        MigrateToV9();
    }

    private void MigrateToV9()
    {
        // Tier-2 recorded-model attribution (T-2026-06-15-04): persist the model that was REQUESTED
        // for a span's work so the trace rollup can price the un-metered case from the recorded model
        // instead of the assumed default. Two capture sources:
        //   - cron: the launched run's request.Model (Scope knows it; no parsing).
        //   - interactive hook: tool_input.model on an Agent dispatch, recorded onto the dispatched
        //     child agent's span (host-shape knowledge stays in ClaudeCodeHookAdapter).
        // It is a REQUESTED/recorded model (precedence Tier 2): wire truth (Tier 1) still wins, and
        // the assumed default (Tier 3) remains the last resort. Forward-only, nullable, no backfill.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_spans ADD COLUMN model TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(9, "Recorded/requested model on obs_agent_spans (Tier-2 cost attribution)");
        MigrateToV10();
    }

    private void MigrateToV10()
    {
        // Privileged 'user_submit' first-class event class (T-2026-06-16-19/ST-01). Every human
        // intervention (initiating prompt, follow-up, interrupt, tool approval/denial, redirection,
        // cron prompt) is recorded as an obs_agent_events row with event_type='user_submit'. Three
        // additive, nullable columns on obs_agent_events:
        //   - event_subtype: the ratified taxonomy member (see UserSubmitSubtype).
        //   - content_hash:  stable SHA-256 over the normalized content; the primary dedup key
        //                    together with session_key so the same submit captured by multiple
        //                    redundant paths (payload/hook/cron) records once.
        //   - turn_index:    0-based ordinal of the submit within the session (initiating = 0),
        //                    a secondary identity for path-specific dedup.
        // Forward-only, nullable, no backfill; legacy event rows stay NULL. A partial UNIQUE index
        // enforces the (session_key, content_hash) dedup invariant at the storage layer, scoped to
        // user_submit rows so other event types (content_hash NULL) are never constrained.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_events ADD COLUMN event_subtype TEXT;",
            "ALTER TABLE obs_agent_events ADD COLUMN content_hash TEXT;",
            "ALTER TABLE obs_agent_events ADD COLUMN turn_index INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_obsagent_usersubmit_dedup
                ON obs_agent_events(session_key, content_hash)
                WHERE event_type = 'user_submit' AND content_hash IS NOT NULL;
                """;
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsagent_subtype ON obs_agent_events(event_subtype);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(10, "Privileged user_submit event class: event_subtype, content_hash, turn_index on obs_agent_events");
        MigrateToV11();
    }

    private void MigrateToV11()
    {
        // D3 session bridge (T-2026-06-19-01 Phase 3b): bind an interactive session's WIRE side to its
        // Scope side. Interactive wire rows carry Claude Code's OWN X-Claude-Code-Session-Id
        // (wire_calls.cc_session_id), NOT Scope's cli-{uuid7} SCOPE_SESSION_KEY. The hook already sees
        // BOTH ids in the same child (the CC session_id in its payload, the Scope key from
        // SCOPE_SESSION_KEY), so it records the CC session id here next to the Scope session key. The
        // header-based wire attribution pass then maps wire_calls.cc_session_id -> this column ->
        // session_key to attribute interactive spend to the right Scope session.
        // Additive, nullable, set-once (COALESCE in UpsertAgentSession), no backfill; content-free (an
        // ephemeral per-spawn id, not content). Legacy/cron rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN cc_session_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_ccsession ON obs_agent_sessions(cc_session_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(11, "D3 session bridge: cc_session_id on obs_agent_sessions (CC session id -> Scope session key)");
        MigrateToV12();
    }

    private void MigrateToV12()
    {
        // Session generations (T-2026-06-19-03 Wave 1): a `/clear` in the `scope claude` wrapper keeps
        // the OS process, so the launcher-minted cli-{uuid7} SCOPE_SESSION_KEY (the BASE) stays frozen
        // and multiple `/clear`-separated conversations collapse into ONE Scope session. Claude Code DOES
        // mint a new session_id on `/clear`, so a previously-unseen cc_session_id under a base is the
        // deterministic boundary signal. This table binds (base, cc) -> a per-base GENERATION ordinal and
        // its derived effective_key (g0 SUFFIXLESS == base; g1+ == base + "#g" + n), allocated once and
        // idempotent under interleave (PK on (base, cc) + IMMEDIATE txn in ResolveEffectiveSessionKey).
        // The unique (base, generation) index guarantees a generation maps to exactly one effective_key.
        // Forward-only: existing single-generation sessions stay byte-identical (no row, g0, no suffix).
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_session_generations (
                base_session_key TEXT NOT NULL,
                cc_session_id    TEXT NOT NULL,
                generation       INTEGER NOT NULL,
                effective_key    TEXT NOT NULL,
                opened_at        TEXT NOT NULL,
                open_source      TEXT,
                PRIMARY KEY (base_session_key, cc_session_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_obssessgen_effective
                ON obs_session_generations(base_session_key, generation);
            """;
        cmd.ExecuteNonQueryRetrying();

        // Stamp the resolved generation provenance onto the session row so a reader can attribute a
        // session to its base/generation without re-deriving. Nullable, forward-only: a NULL generation
        // is read as 0 and a NULL base_session_key as the session_key itself (legacy rows).
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_sessions ADD COLUMN base_session_key TEXT;",
            "ALTER TABLE obs_agent_sessions ADD COLUMN generation INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_base ON obs_agent_sessions(base_session_key);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(12, "Session generations: obs_session_generations + base_session_key/generation on obs_agent_sessions (/clear splits a base into generations)");

        MigrateToV13();
    }

    private void MigrateToV13()
    {
        // SessionStart source precedence (T-2026-06-19-03 Wave 2). The obs_session_generation_hints scratch
        // table that originally backed a CROSS-PROCESS pending no-roll hint was REMOVED in T-2026-06-19-04:
        // that mechanism proved DEAD (the no-roll signal is honored INLINE on the SessionStart's own binding
        // via the openSource argument to ResolveEffectiveSessionKey, so no cross-process scratch is needed).
        // Forward-only: the schema VERSION stays 13 and the rest of this migration body is unchanged, so a
        // live v13 db is never re-migrated and an already-created orphan hints table is harmless (purged
        // separately). A fresh db simply never creates the table; nothing references it.

        // Relax the Wave 1 (base, generation) UNIQUE index to NON-unique. Under compact/resume no-roll a
        // genuinely-new cc id legitimately binds to the CURRENT generation, so two (base, cc) rows now
        // share one generation/effective_key. The 1:1 "a generation maps to exactly one effective_key"
        // property is PRESERVED by construction (effective_key is derived as base + "#g" + generation),
        // but it is now a functional dependency, not a row-uniqueness constraint. Keep the index for the
        // MAX(generation) / current-generation lookups; DROP + recreate non-unique. Both are idempotent.
        try
        {
            using var dropIdx = connection.CreateCommand();
            dropIdx.CommandText = "DROP INDEX IF EXISTS idx_obssessgen_effective;";
            dropIdx.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        try
        {
            using var idx = connection.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssessgen_effective ON obs_session_generations(base_session_key, generation);";
            idx.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(13, "Session generation no-roll: (base, generation) index relaxed to non-unique for compact/resume multi-cc (the obs_session_generation_hints table this version originally created was removed as dead in T-2026-06-19-04; dropped from live dbs in v15)");

        MigrateToV14();
    }

    private void MigrateToV14()
    {
        // Tool-call status (T-2026-06-20-02, OBS-12): persist a machine-readable status for tool_call
        // events so the trace timeline can show success/failed instead of dropping the only signal the
        // host gives. Values written by the capture path (ClaudeCodeHookAdapter): 'success' (a
        // tool_response was present with no error shape), 'failed' (the tool_response carried an error
        // shape -- is_error / error / errorMessage / interrupted, or a Bash nonzero-exit shape), or NULL
        // when undeterminable (never invented). This is an acknowledged HEURISTIC: PostToolUse exposes no
        // explicit status field, so it is derived from the tool_response shape, and host-shape knowledge
        // stays in ClaudeCodeHookAdapter (platform-independence rule).
        //
        // NOTE on 'denied': a tool DENIAL is a human-intervention signal that belongs to the
        // user_submit / Notification path, NOT a tool status. The Notification hook exposes a pending
        // permission decision only as English prose (no structured approved/denied field), so 'denied'
        // is host-blocked today and is deliberately NOT a value this column ever takes. See
        // ClaudeCodeHookAdapter.ParseNotification for the documented gap.
        //
        // Additive, forward-only, no backfill: legacy event rows (and all non-tool_call event types)
        // stay NULL. ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_events ADD COLUMN status TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(14, "Tool-call status (success/failed/NULL) on obs_agent_events (OBS-12; denied is a human-intervention signal, host-blocked, never a tool status)");

        MigrateToV15();
    }

    private void MigrateToV15()
    {
        // Drop the orphaned obs_session_generation_hints table (T-2026-06-20-20b). The table originally
        // backed a CROSS-PROCESS pending no-roll hint (v13) but that mechanism proved DEAD and its CREATE
        // was removed in T-2026-06-19-04 (the no-roll signal is honored INLINE on the SessionStart's own
        // binding via ResolveEffectiveSessionKey's openSource argument; no cross-process scratch is needed).
        // No INSERT and no SELECT reference it anywhere. Removing the CREATE stopped fresh dbs from making
        // it, but live dbs created at or before v13 still carry the orphan physically. This forward-only
        // migration purges it on next schema ensure. DROP TABLE IF EXISTS is idempotent: a fresh db that
        // never created the table is a no-op.
        try
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS obs_session_generation_hints;";
            dropCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* table already absent */ }

        InsertSchemaVersion(15, "Drop orphaned obs_session_generation_hints table (dead since T-2026-06-19-04; no reader/writer)");
    }

    private void InsertSchemaVersion(int version, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO obs_schema_versions (version, applied_at, description)
            VALUES ($version, $appliedAt, $description);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$description", description);
        cmd.ExecuteNonQueryRetrying();
    }
}
